using System;
using System.Collections;
using KBCore.Refs;
using Unity.Cinemachine;
using UnityEngine;
using UnityUtils;

[RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
public class PlayerMover : ValidatedMonoBehaviour
{
    [SerializeField, Self] private Rigidbody _rb;
    [SerializeField, Self] private Transform _tr;
    [SerializeField, Self] private CapsuleCollider _col;
    [SerializeField, Child] private CinemachineCamera _cinemachineCamera;
    [Range(0f, 1f), SerializeField] private float _stepHeightRatio = 0.1f;
    [SerializeField] private float _groundAdjustmentVelocityMultiplier = 0.5f;
    [SerializeField] private float _colliderHeight = 2f;
    [SerializeField] private float _colliderThickness = 1f;
    [SerializeField] private Vector3 _colliderOffset = Vector3.zero;
    [SerializeField] private float _crouchHeightPercentage = .75f;
    [SerializeField] private float _crouchSmoothTime = 0.1f;
    [SerializeField] private float _standUpCheckRadiusMultiplier = 0.9f;
    [SerializeField] private bool _moveCameraOnCrouch = true;
    
    private RaycastSensor _sensor;
    
    public bool IsCrouching { get; private set; }
    public bool IsGrounded { get; private set; }

    private float _baseSensorRange;
    private Vector3 _currentGroundAdjustmentVelocity; // Velocity to adjust player position to maintain ground contact
    private int _currentLayer;
    private bool _isUsingExtendedSensorRange = true;
    private float _standingColliderHeight;
    private float _crouchHeightVelocity;
    private Vector3 _crouchCenterVelocity;
    private float _standingCameraHeight;
    private float _cameraHeightVelocity;
    
    private Coroutine _crouchTransition;

    private void Awake()
    {
        Setup();
        RecalculateColliderDimensions();
    }
    
    private void OnCollisionEnter(Collision collision) => KeepWallDistance(collision);
    private void OnCollisionStay(Collision collision) => KeepWallDistance(collision);

    private void Setup()
    {
        _tr = transform;

        _rb.freezeRotation = true;
        _rb.useGravity = false;
        
        _standingCameraHeight = _cinemachineCamera.transform.localPosition.y;
        _standingColliderHeight = _col.height;
    }


    private void RecalculateSensorLayerMask()
    {
        int objectLayer = gameObject.layer;
        int layerMask = Physics.AllLayers;

        for (int i = 0; i < 32; i++)
        {
            if (Physics.GetIgnoreLayerCollision(objectLayer, i))
            {
                layerMask &= ~(1 << i);
            }
        }

        int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
        layerMask &= ~(1 << ignoreRaycastLayer);

        _sensor.layerMask = layerMask;
        _currentLayer = objectLayer;
    }

    private void RecalibrateSensor()
    {
        _sensor ??= new RaycastSensor(_tr);

        _sensor.SetCastOrigin(_col.bounds.center);
        _sensor.SetCastDirection(RaycastSensor.CastDirection.Down);
        RecalculateSensorLayerMask();

        const float safetyDistanceFactor = 0.001f;

        float length = _colliderHeight * (1f - _stepHeightRatio) * 0.5f + _colliderHeight * _stepHeightRatio;
        _baseSensorRange = length * (1f + safetyDistanceFactor) * _tr.localScale.x;
        _sensor.castLength = length * _tr.localScale.x;
    }

    private void RecalculateColliderDimensions()
    {
        _col.height = _colliderHeight * (1f - _stepHeightRatio);
        _col.radius = _colliderThickness / 2f;
        _col.center = _colliderOffset * _colliderHeight + new Vector3(0f, _stepHeightRatio * _col.height / 2f, 0f);

        if (_col.height / 2f < _col.radius)
        {
            _col.radius = _col.height / 2f;
        }

        RecalibrateSensor();
    }

    public void ToggleCrouch()
    {
        if (IsCrouching && !CanStand()) return;
        
        IsCrouching = !IsCrouching;
        
        float crouchHeight = _standingColliderHeight * _crouchHeightPercentage;
        _colliderHeight = IsCrouching ? crouchHeight : _standingColliderHeight;
        RecalculateColliderDimensions();

        if (!_moveCameraOnCrouch) return;
        if (_crouchTransition != null) StopCoroutine(_crouchTransition);
        _crouchTransition = StartCoroutine(CrouchCameraTransition(IsCrouching));
    }
    
    private IEnumerator CrouchCameraTransition(bool crouch)
    {
        float crouchCameraHeight = _standingCameraHeight * _crouchHeightPercentage;
        float targetCameraHeight = crouch ? crouchCameraHeight : _standingCameraHeight;
        
        while (Math.Abs(_col.height - _colliderHeight) > 0.01f)
        {
            float y = Mathf.SmoothDamp(
                _cinemachineCamera.transform.localPosition.y, targetCameraHeight,
                ref _cameraHeightVelocity, _crouchSmoothTime,
                Mathf.Infinity, Time.fixedDeltaTime);

            _cinemachineCamera.transform.localPosition =
                _cinemachineCamera.transform.localPosition.With(y: y);

            yield return new WaitForEndOfFrame();
        }
    }

    public void CheckForGround()
    {
        if (_currentLayer != gameObject.layer)
        {
            RecalculateSensorLayerMask();
        }

        _currentGroundAdjustmentVelocity = Vector3.zero;
        _sensor.castLength = _isUsingExtendedSensorRange
            ? _baseSensorRange + _colliderHeight * _tr.localScale.x * _stepHeightRatio
            : _baseSensorRange;
        _sensor.Cast();

        IsGrounded = _sensor.HasDetectedHit();
        if (!IsGrounded) return;
        float distance = _sensor.GetDistance();
        float upperLimit = _colliderHeight * _tr.localScale.x * (1f - _stepHeightRatio) * 0.5f;
        float middle = upperLimit + _colliderHeight * _tr.localScale.x * _stepHeightRatio;
        float distanceToGo = middle - distance;

        _currentGroundAdjustmentVelocity =
            _tr.up * ((distanceToGo / Time.fixedDeltaTime) * _groundAdjustmentVelocityMultiplier);
    }

    public void KeepWallDistance(Collision collision)
    {
        if (collision.contacts.Length == 0) return;

        float angle = Vector3.Angle(-transform.up, collision.contacts[0].normal);

        if (angle is < 100f and > 80f)
        {
            Vector3 adjustment = collision.contacts[0].normal * 0.01f;
            Vector3 newPos = _rb.position + adjustment;
            
            _rb.MovePosition(newPos);
        }
        
        Debug.DrawRay(collision.contacts[0].point, collision.contacts[0].normal, Color.blue, 2f);
    }
    
    private bool CanStand()
    {
        Vector3 point1 = _rb.position + _col.center + Vector3.up * (_col.height / 2 - _col.radius);
        Vector3 point2 = _rb.position + Vector3.up * (_standingColliderHeight - _col.radius);

        float radius = _col.radius * _standUpCheckRadiusMultiplier;
        float distanceAdjustment = _col.radius - radius;

        Vector3 direction = point2 - point1;
        float distance = direction.magnitude + distanceAdjustment;
        direction.Normalize();


        RaycastHit[] hits = new RaycastHit[10];
        int size = Physics.SphereCastNonAlloc(point1, radius, direction, hits, distance);
        for (int i = 0; i < size; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider.CompareTag("Player")) continue;
            if (hit.collider.attachedRigidbody && !hit.collider.attachedRigidbody.isKinematic) continue;
            return false;
        }

        return true;
    }
    
    public Vector3 GetGroundNormal() => _sensor.GetNormal();

    public void SetVelocity(Vector3 velocity) => _rb.linearVelocity = velocity + _currentGroundAdjustmentVelocity;
    public void SetExtendSensorRange(bool isExtended) => _isUsingExtendedSensorRange = isExtended;

    public void SetRotation(float x = 0, float y = 0, float z = 0)
    {
        x = x == 0 ? _rb.rotation.eulerAngles.x : x;
        y = y == 0 ? _rb.rotation.eulerAngles.y : y;
        z = z == 0 ? _rb.rotation.eulerAngles.z : z;

        _rb.MoveRotation(Quaternion.Euler(x, y, z));
    }
}