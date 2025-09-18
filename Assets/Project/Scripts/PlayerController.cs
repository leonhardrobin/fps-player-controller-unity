using System;
using ImprovedTimers;
using KBCore.Refs;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityUtils;
using UnityUtils.StateMachine;


public class PlayerController : StatefulEntity
{
    public event Action<Vector2, bool> OnMove;
    public event Action<Vector3> OnJump;
    public event Action<Vector3> OnLand;

    [Header("Movement")]
    [SerializeField, Self] private PlayerInput _input;
    [SerializeField, Self] private Transform _tr;
    [SerializeField, Self] private PlayerMover _mover;
    [SerializeField, Self] private CeilingDetector _ceilingDetector;
    [SerializeField, Child] private Transform _cameraTransform;
    [SerializeField, Child] private CinemachinePanTilt _cinemachinePanTilt;
    [SerializeField] private float _movementSpeed = 4f;
    [SerializeField] private float _sprintMultiplier = 1.35f;
    [SerializeField] private float _crouchMultiplier = 0.75f;
    [SerializeField] private float _multiplierSmoothing = 10f;
    [SerializeField] private float _airControlRate = 5f;
    [SerializeField] private float _jumpSpeed = 5f;
    [SerializeField] private float _jumpDuration = 0.2f;
    [SerializeField] private float _airFriction = 0.5f;
    [SerializeField] private float _groundFriction = 100f;
    [SerializeField] private float _gravity = 30f;
    [SerializeField] private float _slideGravity = 5f;
    [SerializeField] private float _slopeLimit = 30f;
    [SerializeField] private bool _useLocalMomentum;

    private Vector2 Direction => _input.actions["Move"].ReadValue<Vector2>();
    private InputAction Jump => _input.actions["Jump"];
    private InputAction Sprint => _input.actions["Sprint"];
    private InputAction Crouch => _input.actions["Crouch"];

    private Vector3 _momentum, _savedVelocity, _savedMovementVelocity;
    private float _currentSprintMultiplier = 1f;
    private bool _enableSprinting = true;
    private bool _enableCrouching = true;
    private float _cachedMovementSpeed;

    private CountdownTimer _jumpTimer;

    public bool IsSprinting { get; private set; }

    private void OnValidate() => this.ValidateRefs();

    protected override void Awake()
    {
        base.Awake();

        _jumpTimer = new CountdownTimer(_jumpDuration);
        SetupStateMachine();

        Crouch.started += HandleCrouch;
        Crouch.canceled += HandleCrouch;

        _cachedMovementSpeed = _movementSpeed;
    }

    private void Start()
    {
        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.Locked;
    }

    private void OnDisable()
    {
        if (!_input) return;

        Crouch.started -= HandleCrouch;
        Crouch.canceled -= HandleCrouch;
    }

    private void OnCollisionEnter(Collision collision) => HandleFallingCollision(collision);

    private void OnCollisionStay(Collision collision) => HandleFallingCollision(collision);

    protected override void Update()
    {
        base.Update();
        HandleSpeedMultiplier();
    }

    protected override void FixedUpdate()
    {
        base.FixedUpdate();

        HandleRotation();

        _mover.CheckForGround();
        HandleMomentum();
        Vector3 velocity = stateMachine.CurrentState is GroundedState ? CalculateMovementVelocity() : Vector3.zero;
        velocity += _useLocalMomentum ? _tr.localToWorldMatrix * _momentum : _momentum;

        OnMove?.Invoke(Direction, IsSprinting);
        
        _mover.SetExtendSensorRange(IsGrounded());
        _mover.SetVelocity(velocity);

        _savedVelocity = velocity;
        _savedMovementVelocity = CalculateMovementVelocity();

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
        At<Func<bool>>(grounded, sliding, () => _mover.IsGrounded && IsGroundTooSteep());
        At<Func<bool>>(grounded, falling, () => !_mover.IsGrounded);
        At<Func<bool>>(grounded, jumping, () => Jump.IsPressed());

        At<Func<bool>>(falling, rising, IsRising);
        At<Func<bool>>(falling, grounded, () => _mover.IsGrounded && !IsGroundTooSteep());
        At<Func<bool>>(falling, sliding, () => _mover.IsGrounded && IsGroundTooSteep());

        At<Func<bool>>(sliding, rising, IsRising);
        At<Func<bool>>(sliding, falling, () => !_mover.IsGrounded);
        At<Func<bool>>(sliding, grounded, () => _mover.IsGrounded && !IsGroundTooSteep());

        At<Func<bool>>(rising, grounded, () => _mover.IsGrounded && !IsGroundTooSteep());
        At<Func<bool>>(rising, sliding, () => _mover.IsGrounded && IsGroundTooSteep());
        At<Func<bool>>(rising, falling, IsFalling);
        At<Func<bool>>(rising, falling, () => _ceilingDetector && _ceilingDetector.HitCeiling());

        At<Func<bool>>(jumping, rising, () => _jumpTimer.IsFinished);
        At<Func<bool>>(jumping, falling, () => _ceilingDetector && _ceilingDetector.HitCeiling());

        stateMachine.SetState(falling);
    }

    private bool IsRising() => VectorMath.GetDotProduct(GetMomentum(), _tr.up) > 0f;
    private bool IsFalling() => VectorMath.GetDotProduct(GetMomentum(), _tr.up) < 0f;

    private bool IsGroundTooSteep() =>
        !_mover.IsGrounded || Vector3.Angle(_mover.GetGroundNormal(), _tr.up) > _slopeLimit;

    private Vector3 CalculateMovementVelocity() =>
        CalculateMovementDirection() * (_movementSpeed * _currentSprintMultiplier);

    private Vector3 CalculateMovementDirection()
    {
        Vector3 direction = !_cameraTransform
            ? _tr.right * Direction.x + _tr.forward * Direction.y
            : Vector3.ProjectOnPlane(_cameraTransform.right, _tr.up).normalized * Direction.x +
              Vector3.ProjectOnPlane(_cameraTransform.forward, _tr.up).normalized * Direction.y;

        return direction.magnitude > 1f ? direction.normalized : direction;
    }

    private void HandleSpeedMultiplier()
    {
        if (_enableCrouching && _mover.IsCrouching)
        {
            _currentSprintMultiplier = Mathf.Lerp(_currentSprintMultiplier, _crouchMultiplier, Time.deltaTime * _multiplierSmoothing);
        }
        else if (_enableSprinting && Sprint.IsPressed())
        {
            _currentSprintMultiplier = Mathf.Lerp(_currentSprintMultiplier, _sprintMultiplier, Time.deltaTime * _multiplierSmoothing);
        }
        else
        {
            _currentSprintMultiplier = Mathf.Lerp(_currentSprintMultiplier, 1f, Time.deltaTime * _multiplierSmoothing);
        }

        IsSprinting = _currentSprintMultiplier > 1 && _mover.IsMoving;
    }

    private void HandleCrouch(InputAction.CallbackContext context)
    {
        if (!context.started) return;
        _mover.ToggleCrouch();
    }

    private void HandleFallingCollision(Collision collision)
    {
        if (stateMachine.CurrentState is not FallingState) return;
        if (!Mathf.Approximately(VectorMath.ExtractDotVector(_momentum, _tr.up).magnitude, 0f)) return;

        _mover.KeepWallDistance(collision);
    }

    private void HandleMomentum()
    {
        if (_useLocalMomentum) _momentum = _tr.localToWorldMatrix * _momentum;

        Vector3 verticalMomentum = VectorMath.ExtractDotVector(_momentum, _tr.up);
        Vector3 horizontalMomentum = _momentum - verticalMomentum;

        verticalMomentum -= _tr.up * (_gravity * Time.deltaTime);
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

        float friction = stateMachine.CurrentState is GroundedState ? _groundFriction : _airFriction;
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
            _momentum += slideDirection * (_slideGravity * Time.deltaTime);
        }

        if (_useLocalMomentum) _momentum = _tr.worldToLocalMatrix * _momentum;
    }

    private void HandleRotation()
    {
        _mover.SetRotation(y: _cinemachinePanTilt.PanAxis.Value);
    }

    private bool IsGrounded() => stateMachine.CurrentState is GroundedState or SlidingState;

    private void HandleJumping()
    {
        _momentum = VectorMath.RemoveDotVector(_momentum, _tr.up);
        _momentum += _tr.up * _jumpSpeed;
    }

    private void AdjustHorizontalMomentum(ref Vector3 horizontalMomentum, Vector3 movementVelocity)
    {
        if (horizontalMomentum.magnitude > _movementSpeed)
        {
            if (VectorMath.GetDotProduct(movementVelocity, horizontalMomentum.normalized) > 0f)
            {
                movementVelocity = VectorMath.RemoveDotVector(movementVelocity, horizontalMomentum.normalized);
            }

            horizontalMomentum += movementVelocity * (Time.deltaTime * _airControlRate * 0.25f);
        }
        else
        {
            horizontalMomentum += movementVelocity * (Time.deltaTime * _airControlRate);
            horizontalMomentum = Vector3.ClampMagnitude(horizontalMomentum, _movementSpeed);
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
        Vector3 collisionVelocity = _useLocalMomentum ? _tr.localToWorldMatrix * _momentum : _momentum;
        OnLand?.Invoke(collisionVelocity);
    }

    public void OnFallStart()
    {
        Vector3 currentUpMomentum = VectorMath.ExtractDotVector(_momentum, _tr.up);
        _momentum = VectorMath.RemoveDotVector(_momentum, _tr.up);
        _momentum -= _tr.up * currentUpMomentum.magnitude;
    }

    public void OnGroundContactLost()
    {
        if (_useLocalMomentum) _momentum = _tr.localToWorldMatrix * _momentum;

        Vector3 velocity = GetMovementVelocity();
        if (velocity.sqrMagnitude >= 0f && _momentum.sqrMagnitude > 0f)
        {
            Vector3 projectedMomentum = Vector3.Project(_momentum, velocity.normalized);
            float dot = VectorMath.GetDotProduct(projectedMomentum.normalized, velocity.normalized);

            if (projectedMomentum.sqrMagnitude >= velocity.sqrMagnitude && dot > 0f) velocity = Vector3.zero;
            else if (dot > 0f) velocity -= projectedMomentum;
        }

        _momentum += velocity;

        if (_useLocalMomentum) _momentum = _tr.worldToLocalMatrix * _momentum;
    }

    public void OnJumpStart()
    {
        if (_useLocalMomentum) _momentum = _tr.localToWorldMatrix * _momentum;

        _momentum += _tr.up * _jumpSpeed;
        _jumpTimer.Start();
        OnJump?.Invoke(_momentum);

        if (_useLocalMomentum) _momentum = _tr.worldToLocalMatrix * _momentum;
    }


    public Vector3 GetVelocity() => _savedVelocity;
    
    public Vector3 GetMomentum() => _useLocalMomentum ? _tr.localToWorldMatrix * _momentum : _momentum;
    
    public Vector3 GetMovementVelocity() => _savedMovementVelocity;
    
    public IState GetState() => stateMachine.CurrentState;
    
    public void SetTemporaryMovementSpeedMultiplier(float multiplier) => _movementSpeed *= multiplier;

    public void ResetTemporaryMovementSpeed() => _movementSpeed = _cachedMovementSpeed;
    
    public void EnableSprinting(bool enable) => _enableSprinting = enable;

    public void EnableCrouching(bool enable) => _enableCrouching = enable;
}