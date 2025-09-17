/*
 * Author: Leonhard Schnaitl
 * GitHub: https://github.com/leonhardrobin
 */

using UnityEngine;
using System;
using System.Collections;
using Unity.Cinemachine;
using UnityEngine.InputSystem;
using UnityUtils;
using Cursor = UnityEngine.Cursor;

namespace Simple
{
    [DisallowMultipleComponent, RequireComponent(typeof(PlayerInput), typeof(Rigidbody), typeof(CapsuleCollider))]
    public class PlayerController : MonoBehaviour
    {
        #region PUBLIC MEMBERS

        [Serializable]
        public class MovementSettings
        {
            public float walkSpeed = 100;
            public float sprintSpeedMultiplier = 2f;
            public float crouchSpeedMultiplier = 0.75f;
            public float jumpHeight = 0.5f;
            public float crouchHeightPercentage = .5f;
            public float maximumVelocity = 300f;
            public bool enableAirControl = true;
            public bool enableSprinting = true;
            public bool enableCrouching = true;
        }

        [Serializable]
        public class CameraReferences
        {
            public Transform cameraTransform;
            public CinemachineCamera cinemachineCamera;
            public CinemachinePanTilt cinemachinePanTilt;
        }

        [Serializable]
        public class AdvancedSettings
        {
            [Header("Ground Checking")]
            public float sphereCheckRadiusMultiplier = 0.95f;
            public float groundCheckDistance = 0.03f;
            public LayerMask groundCheckLayerMask = ~0;

            [Header("Crouching")]
            public float crouchSmoothTime = 0.1f;
            public float crouchAdjustmentForce = 50f;
            public float standUpCheckRadiusMultiplier = 0.9f;
            public bool moveCameraOnCrouch = true;
            public LayerMask canStandLayerMask = ~0;
        }

        public bool IsSprinting { get; private set; }
        public bool IsMoving { get; private set; }
        public bool IsCrouching { get; private set; }
        public bool IsGrounded { get; private set; }

        public event Action<Vector2, bool> OnMove;
        public event Action<bool> OnCrouchChange;
        public event Action OnJump;

        public LayerMask StandUpLayerMask
        {
            get => _advancedSettings.canStandLayerMask;
            set => _advancedSettings.canStandLayerMask = value;
        }

        public LayerMask GroundCheckLayerMask
        {
            get => _advancedSettings.groundCheckLayerMask;
            set => _advancedSettings.groundCheckLayerMask = value;
        }

        #endregion

        #region PRIVTE MEMBERS

        [SerializeField] private bool _pauseMovement;
        [SerializeField] private MovementSettings _movementSettings = new();
        [SerializeField] private CameraReferences _cameraReferences = new();
        [SerializeField] private AdvancedSettings _advancedSettings = new();

        private Vector2 Direction => _input.actions["Move"].ReadValue<Vector2>();
        private InputAction Sprint => _input.actions["Sprint"];
        private InputAction Jump => _input.actions["Jump"];
        private InputAction Crouch => _input.actions["Crouch"];

        private Rigidbody _rb;
        private CapsuleCollider _col;
        private PlayerInput _input;

        private Vector3 _moveDirection;
        private Coroutine _crouchTransition;

        private const string _PLAYER_TAG = "Player";
        private float _normalColliderHeight;
        private float _crouchHeightVelocity;
        private Vector3 _normalColliderCenter;
        private Vector3 _crouchCenterVelocity;
        private float _normalCameraHeight;
        private float _cameraHeightVelocity;
        private float _movementSpeed;

        #endregion

        #region UNITY MESSAGES

        private void OnValidate()
        {
            if (_cameraReferences.cinemachineCamera &&
                _cameraReferences.cinemachineCamera.TryGetComponent(out CinemachinePanTilt cinemachinePanTilt))
            {
                _cameraReferences.cinemachinePanTilt = cinemachinePanTilt;
            }
        }

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _col = GetComponent<CapsuleCollider>();
            _input = GetComponent<PlayerInput>();

            _normalColliderHeight = _col.height;
            _normalColliderCenter = _col.center;
            _normalCameraHeight = _cameraReferences.cinemachineCamera.transform.localPosition.y;
            _movementSpeed = _movementSettings.walkSpeed;

            gameObject.tag = _PLAYER_TAG;

            Jump.started += HandleJump;
            Crouch.started += HandleCrouch;
            Crouch.canceled += HandleCrouch;

            CreateNonFrictionPhysicsMaterial();
            SetCursor(false);
            _rb.freezeRotation = true;
        }

        // Update is called once per frame
        private void Update()
        {
            if (_pauseMovement) return;

            CheckForGround();
            CalculateMovementDirection();
        }

        private void FixedUpdate()
        {
            if (_pauseMovement) return;

            Movement();
            Rotation();
        }

        private void OnDisable()
        {
            if (!_input) return;

            Jump.started -= HandleJump;
            Crouch.started -= HandleCrouch;
            Crouch.canceled -= HandleCrouch;
        }

        #endregion

        #region PRIVATE METHODS

        private void CreateNonFrictionPhysicsMaterial()
        {
            PhysicsMaterial physicsMaterial = new()
            {
                dynamicFriction = 0,
                staticFriction = 0,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
            _col.material = physicsMaterial;
        }

        private void CalculateMovementDirection()
        {
            Vector3 direction = _cameraReferences.cameraTransform == null
                ? transform.right * Direction.x + transform.forward * Direction.y
                : Vector3.ProjectOnPlane(_cameraReferences.cameraTransform.right, transform.up).normalized *
                  Direction.x +
                  Vector3.ProjectOnPlane(_cameraReferences.cameraTransform.forward, transform.up).normalized *
                  Direction.y;

            _moveDirection = direction.magnitude > 1f ? direction.normalized : direction;
            IsMoving = direction.magnitude > 0f;
        }

        private void Movement()
        {
            float speedMultiplier = 1;
            if (_movementSettings.enableSprinting && Sprint.IsPressed())
            {
                speedMultiplier = _movementSettings.sprintSpeedMultiplier;
            }

            if (_movementSettings.enableCrouching && IsCrouching)
            {
                speedMultiplier = _movementSettings.crouchSpeedMultiplier;
            }

            IsSprinting = speedMultiplier > 1 && IsMoving;

            if (!_movementSettings.enableAirControl && !IsGrounded) return;
            if (_rb.isKinematic) return;

            OnMove?.Invoke(Direction, IsSprinting);

            Vector3 moveVelocity = _moveDirection * (_movementSpeed * speedMultiplier * Time.fixedDeltaTime);
            moveVelocity = Vector3.ClampMagnitude(moveVelocity, _movementSettings.maximumVelocity);

            _rb.linearVelocity = new Vector3(moveVelocity.x, _rb.linearVelocity.y, moveVelocity.z);
        }

        private void HandleJump(InputAction.CallbackContext context)
        {
            if (context.started && IsGrounded)
            {
                _rb.linearVelocity =
                    new Vector3(0, Mathf.Sqrt(_movementSettings.jumpHeight * -2f * Physics.gravity.y), 0);
                OnJump?.Invoke();
            }
        }

        private void HandleCrouch(InputAction.CallbackContext context)
        {
            if (_pauseMovement) return;
            if (!_movementSettings.enableCrouching) return;
            if (!context.started) return;

            if (IsCrouching && !CanStand()) return;

            IsCrouching = !IsCrouching;
            OnCrouchChange?.Invoke(IsCrouching);
            if (_crouchTransition != null) StopCoroutine(_crouchTransition);
            _crouchTransition = StartCoroutine(CrouchTransition(IsCrouching));
        }

        private IEnumerator CrouchTransition(bool crouch)
        {
            float crouchHeight = _normalColliderHeight * _movementSettings.crouchHeightPercentage;
            float targetHeight = crouch ? crouchHeight : _normalColliderHeight;

            Vector3 crouchCenter = _normalColliderCenter * _movementSettings.crouchHeightPercentage;
            Vector3 targetCenter = crouch ? crouchCenter : _normalColliderCenter;

            float crouchCameraHeight = _normalCameraHeight * _movementSettings.crouchHeightPercentage;
            float targetCameraHeight = crouch ? crouchCameraHeight : _normalCameraHeight;


            while (Math.Abs(_col.height - targetHeight) > 0.01f)
            {
                _col.height = Mathf.SmoothDamp(_col.height, targetHeight, ref _crouchHeightVelocity,
                    _advancedSettings.crouchSmoothTime, Mathf.Infinity, Time.fixedDeltaTime);

                _col.center = Vector3.SmoothDamp(_col.center, targetCenter, ref _crouchCenterVelocity,
                    _advancedSettings.crouchSmoothTime, Mathf.Infinity, Time.fixedDeltaTime);

                if (_advancedSettings.moveCameraOnCrouch)
                {
                    float y = Mathf.SmoothDamp(
                        _cameraReferences.cinemachineCamera.transform.localPosition.y, targetCameraHeight,
                        ref _cameraHeightVelocity, _advancedSettings.crouchSmoothTime,
                        Mathf.Infinity, Time.fixedDeltaTime);

                    _cameraReferences.cinemachineCamera.transform.localPosition =
                        _cameraReferences.cinemachineCamera.transform.localPosition.With(y: y);
                }

                if (crouch)
                {
                    _rb.AddForce(Vector3.down * _advancedSettings.crouchAdjustmentForce, ForceMode.Impulse);
                }

                yield return new WaitForEndOfFrame();
            }
        }

        private void Rotation()
        {
            Quaternion rot = Quaternion.Euler(_rb.rotation.eulerAngles.x,
                _cameraReferences.cinemachinePanTilt.PanAxis.Value,
                _rb.rotation.eulerAngles.z);
            _rb.MoveRotation(rot);
        }

        #region CALCULATIONS

        private void CheckForGround()
        {
            float distanceToPoints = _col.height / 2 - _col.radius;
            Vector3 spherePos = transform.position + _col.center - Vector3.up * distanceToPoints;

            spherePos -= new Vector3(0, _advancedSettings.groundCheckDistance, 0);

            float radius = _col.radius * _advancedSettings.sphereCheckRadiusMultiplier;

            RaycastHit[] hits = new RaycastHit[10];
            int size = Physics.SphereCastNonAlloc(spherePos, radius, Vector3.down, hits,
                _advancedSettings.groundCheckDistance, _advancedSettings.groundCheckLayerMask);

            for (int i = 0; i < size; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider.enabled && !hit.collider.CompareTag(_PLAYER_TAG))
                {
                    IsGrounded = true;
                    return;
                }
            }

            IsGrounded = false;
        }

        private bool CanStand()
        {
            Vector3 point1 = _rb.position + _col.center + Vector3.up * (_col.height / 2 - _col.radius);
            Vector3 point2 = _rb.position + Vector3.up * (_normalColliderHeight - _col.radius);

            float radius = _col.radius * _advancedSettings.standUpCheckRadiusMultiplier;
            float distanceAdjustment = _col.radius - radius;

            Vector3 direction = point2 - point1;
            float distance = direction.magnitude + distanceAdjustment;
            direction.Normalize();


            RaycastHit[] hits = new RaycastHit[10];
            int size = Physics.SphereCastNonAlloc(point1, radius, direction, hits, distance,
                _advancedSettings.canStandLayerMask);
            for (int i = 0; i < size; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider.CompareTag(_PLAYER_TAG)) continue;
                if (hit.collider.attachedRigidbody && !hit.collider.attachedRigidbody.isKinematic) continue;
                return false;
            }

            return true;
        }

        #endregion

        #endregion

        #region PUBLIC METHODS

        public void SetKinematic(bool freeze)
        {
            _rb.isKinematic = freeze;
        }

        public void PauseMovement(bool pause)
        {
            _cameraReferences.cinemachinePanTilt.enabled = !pause;
            _pauseMovement = pause;
        }

        public void SetCursor(bool visible)
        {
            Cursor.visible = visible;
            Cursor.lockState = visible ? CursorLockMode.None : CursorLockMode.Locked;
        }

        public void SetTemporaryMovementSpeedMultiplier(float multiplier) =>
            _movementSpeed = _movementSettings.walkSpeed * multiplier;

        public void ResetTemporaryMovementSpeed() => _movementSpeed = _movementSettings.walkSpeed;

        public void EnableSprinting(bool enable) => _movementSettings.enableSprinting = enable;

        public void EnableCrouching(bool enable) => _movementSettings.enableCrouching = enable;

        public Vector3 GetVelocity() => _rb.linearVelocity;
        
        public MovementSettings GetMovementSettings() => _movementSettings;

        #endregion
    }
}