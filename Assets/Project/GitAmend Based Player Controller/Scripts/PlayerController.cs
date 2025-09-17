using System;
using ImprovedTimers;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityUtils;
using UnityUtils.StateMachine;

namespace GitAmend
{
    public class PlayerController : StatefulEntity
    {
        #region Fields

        private PlayerInput _input;

        private Transform _tr;
        private PlayerMover _mover;
        private CeilingDetector _ceilingDetector;

        private bool _jumpKeyIsPressed; // Tracks whether the jump key is currently being held down by the player

        private bool
            _jumpKeyWasPressed; // Indicates if the jump key was pressed since the last reset, used to detect jump initiation

        private bool
            _jumpKeyWasLetGo; // Indicates if the jump key was released since it was last pressed, used to detect when to stop jumping

        private bool
            _jumpInputIsLocked; // Prevents jump initiation when true, used to ensure only one jump action per press

        [Header("Movement")]
        public float movementSpeed = 7f;
        public float airControlRate = 2f;
        public float jumpSpeed = 10f;
        public float jumpDuration = 0.2f;
        public float airFriction = 0.5f;
        public float groundFriction = 100f;
        public float gravity = 30f;
        public float slideGravity = 5f;
        public float slopeLimit = 30f;
        public bool useLocalMomentum;

        private Vector2 Direction => _input.actions["Move"].ReadValue<Vector2>();
        private InputAction Jump => _input.actions["Jump"];

        private CountdownTimer _jumpTimer;

        [SerializeField] private Transform _cameraTransform;
        [SerializeField] private CinemachinePanTilt _cinemachinePanTilt;

        private Vector3 _momentum, _savedVelocity, _savedMovementVelocity;

        public event Action<Vector3> OnJump = delegate { };
        public event Action<Vector3> OnLand = delegate { };

        #endregion

        public Vector3 GetVelocity() => _savedVelocity;
        public Vector3 GetMomentum() => useLocalMomentum ? _tr.localToWorldMatrix * _momentum : _momentum;
        public Vector3 GetMovementVelocity() => _savedMovementVelocity;

        protected override void Awake()
        {
            base.Awake();

            _tr = transform;
            _mover = GetComponent<PlayerMover>();
            _input = GetComponent<PlayerInput>();
            _ceilingDetector = GetComponent<CeilingDetector>();

            _jumpTimer = new CountdownTimer(jumpDuration);
            SetupStateMachine();
        }

        private void Start()
        {
            Jump.started += (_) => HandleJumpKeyInput(true);
            Jump.canceled += (_) => HandleJumpKeyInput(false);

            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }

        protected override void FixedUpdate()
        {
            base.FixedUpdate();
            
            HandleRotation();
            
            _mover.CheckForGround();
            HandleMomentum();
            Vector3 velocity = stateMachine.CurrentState is GroundedState ? CalculateMovementVelocity() : Vector3.zero;
            velocity += useLocalMomentum ? _tr.localToWorldMatrix * _momentum : _momentum;

            _mover.SetExtendSensorRange(IsGrounded());
            _mover.SetVelocity(velocity);

            _savedVelocity = velocity;
            _savedMovementVelocity = CalculateMovementVelocity();

            ResetJumpKeys();

            if (_ceilingDetector) _ceilingDetector.Reset();
        }

        private void SetupStateMachine()
        {
            GroundedState grounded = new(this);
            FallingState falling = new(this);
            SlidingState sliding = new(this);
            RisingState rising = new(this);
            JumpingState jumping = new(this);

            At<Func<bool>>(grounded, rising, IsRising);
            At<Func<bool>>(grounded, sliding, () => _mover.IsGrounded() && IsGroundTooSteep());
            At<Func<bool>>(grounded, falling, () => !_mover.IsGrounded());
            At<Func<bool>>(grounded, jumping, () => (_jumpKeyIsPressed || _jumpKeyWasPressed) && !_jumpInputIsLocked);

            At<Func<bool>>(falling, rising, IsRising);
            At<Func<bool>>(falling, grounded, () => _mover.IsGrounded() && !IsGroundTooSteep());
            At<Func<bool>>(falling, sliding, () => _mover.IsGrounded() && IsGroundTooSteep());

            At<Func<bool>>(sliding, rising, IsRising);
            At<Func<bool>>(sliding, falling, () => !_mover.IsGrounded());
            At<Func<bool>>(sliding, grounded, () => _mover.IsGrounded() && !IsGroundTooSteep());

            At<Func<bool>>(rising, grounded, () => _mover.IsGrounded() && !IsGroundTooSteep());
            At<Func<bool>>(rising, sliding, () => _mover.IsGrounded() && IsGroundTooSteep());
            At<Func<bool>>(rising, falling, IsFalling);
            At<Func<bool>>(rising, falling, () => _ceilingDetector != null && _ceilingDetector.HitCeiling());

            At<Func<bool>>(jumping, rising, () => _jumpTimer.IsFinished || _jumpKeyWasLetGo);
            At<Func<bool>>(jumping, falling, () => _ceilingDetector != null && _ceilingDetector.HitCeiling());

            stateMachine.SetState(falling);
        }

        private bool IsRising() => VectorMath.GetDotProduct(GetMomentum(), _tr.up) > 0f;
        private bool IsFalling() => VectorMath.GetDotProduct(GetMomentum(), _tr.up) < 0f;

        private bool IsGroundTooSteep() =>
            !_mover.IsGrounded() || Vector3.Angle(_mover.GetGroundNormal(), _tr.up) > slopeLimit;

        private Vector3 CalculateMovementVelocity() => CalculateMovementDirection() * movementSpeed;

        private Vector3 CalculateMovementDirection()
        {
            Vector3 direction = _cameraTransform == null
                ? _tr.right * Direction.x + _tr.forward * Direction.y
                : Vector3.ProjectOnPlane(_cameraTransform.right, _tr.up).normalized * Direction.x +
                  Vector3.ProjectOnPlane(_cameraTransform.forward, _tr.up).normalized * Direction.y;

            return direction.magnitude > 1f ? direction.normalized : direction;
        }

        private void HandleMomentum()
        {
            if (useLocalMomentum) _momentum = _tr.localToWorldMatrix * _momentum;

            Vector3 verticalMomentum = VectorMath.ExtractDotVector(_momentum, _tr.up);
            Vector3 horizontalMomentum = _momentum - verticalMomentum;

            verticalMomentum -= _tr.up * (gravity * Time.deltaTime);
            if (stateMachine.CurrentState is GroundedState && VectorMath.GetDotProduct(verticalMomentum, _tr.up) < 0f)
            {
                verticalMomentum = Vector3.zero;
            }

            if (!IsGrounded())
            {
                AdjustHorizontalMomentum(ref horizontalMomentum, CalculateMovementVelocity());
            }

            if (stateMachine.CurrentState is SlidingState)
            {
                HandleSliding(ref horizontalMomentum);
            }

            float friction = stateMachine.CurrentState is GroundedState ? groundFriction : airFriction;
            horizontalMomentum = Vector3.MoveTowards(horizontalMomentum, Vector3.zero, friction * Time.deltaTime);

            _momentum = horizontalMomentum + verticalMomentum;

            if (stateMachine.CurrentState is JumpingState)
            {
                HandleJumping();
            }

            if (stateMachine.CurrentState is SlidingState)
            {
                _momentum = Vector3.ProjectOnPlane(_momentum, _mover.GetGroundNormal());
                if (VectorMath.GetDotProduct(_momentum, _tr.up) > 0f)
                {
                    _momentum = VectorMath.RemoveDotVector(_momentum, _tr.up);
                }

                Vector3 slideDirection = Vector3.ProjectOnPlane(-_tr.up, _mover.GetGroundNormal()).normalized;
                _momentum += slideDirection * (slideGravity * Time.deltaTime);
            }

            if (useLocalMomentum) _momentum = _tr.worldToLocalMatrix * _momentum;
        }

        private void HandleRotation()
        {
            _mover.SetRotation(y: _cinemachinePanTilt.PanAxis.Value);
        }

        private bool IsGrounded() => stateMachine.CurrentState is GroundedState or SlidingState;

        private void HandleJumping()
        {
            _momentum = VectorMath.RemoveDotVector(_momentum, _tr.up);
            _momentum += _tr.up * jumpSpeed;
        }

        private void HandleJumpKeyInput(bool isButtonPressed)
        {
            if (!_jumpKeyIsPressed && isButtonPressed)
            {
                _jumpKeyWasPressed = true;
            }

            if (_jumpKeyIsPressed && !isButtonPressed)
            {
                _jumpKeyWasLetGo = true;
                _jumpInputIsLocked = false;
            }

            _jumpKeyIsPressed = isButtonPressed;
        }

        private void ResetJumpKeys()
        {
            _jumpKeyWasLetGo = false;
            _jumpKeyWasPressed = false;
        }

        private void AdjustHorizontalMomentum(ref Vector3 horizontalMomentum, Vector3 movementVelocity)
        {
            if (horizontalMomentum.magnitude > movementSpeed)
            {
                if (VectorMath.GetDotProduct(movementVelocity, horizontalMomentum.normalized) > 0f)
                {
                    movementVelocity = VectorMath.RemoveDotVector(movementVelocity, horizontalMomentum.normalized);
                }

                horizontalMomentum += movementVelocity * (Time.deltaTime * airControlRate * 0.25f);
            }
            else
            {
                horizontalMomentum += movementVelocity * (Time.deltaTime * airControlRate);
                horizontalMomentum = Vector3.ClampMagnitude(horizontalMomentum, movementSpeed);
            }
        }

        private void HandleSliding(ref Vector3 horizontalMomentum)
        {
            Vector3 pointDownVector = Vector3.ProjectOnPlane(_mover.GetGroundNormal(), _tr.up).normalized;
            Vector3 movementVelocity = CalculateMovementVelocity();
            movementVelocity = VectorMath.RemoveDotVector(movementVelocity, pointDownVector);
            horizontalMomentum += movementVelocity * Time.fixedDeltaTime;
        }

        public void OnGroundContactRegained()
        {
            Vector3 collisionVelocity = useLocalMomentum ? _tr.localToWorldMatrix * _momentum : _momentum;
            OnLand.Invoke(collisionVelocity);
        }

        public void OnFallStart()
        {
            Vector3 currentUpMomentum = VectorMath.ExtractDotVector(_momentum, _tr.up);
            _momentum = VectorMath.RemoveDotVector(_momentum, _tr.up);
            _momentum -= _tr.up * currentUpMomentum.magnitude;
        }

        public void OnGroundContactLost()
        {
            if (useLocalMomentum) _momentum = _tr.localToWorldMatrix * _momentum;

            Vector3 velocity = GetMovementVelocity();
            if (velocity.sqrMagnitude >= 0f && _momentum.sqrMagnitude > 0f)
            {
                Vector3 projectedMomentum = Vector3.Project(_momentum, velocity.normalized);
                float dot = VectorMath.GetDotProduct(projectedMomentum.normalized, velocity.normalized);

                if (projectedMomentum.sqrMagnitude >= velocity.sqrMagnitude && dot > 0f) velocity = Vector3.zero;
                else if (dot > 0f) velocity -= projectedMomentum;
            }

            _momentum += velocity;

            if (useLocalMomentum) _momentum = _tr.worldToLocalMatrix * _momentum;
        }

        public void OnJumpStart()
        {
            if (useLocalMomentum) _momentum = _tr.localToWorldMatrix * _momentum;

            _momentum += _tr.up * jumpSpeed;
            _jumpTimer.Start();
            _jumpInputIsLocked = true;
            OnJump.Invoke(_momentum);

            if (useLocalMomentum) _momentum = _tr.worldToLocalMatrix * _momentum;
        }
    }
}