using UnityEngine;
using UnityUtils;

public class ControllerDebugger : MonoBehaviour
{
    [SerializeField] private PlayerController _controller;

    private float _topSpeedHorizontal;
    private float _topSpeedVertical;

    private void OnGUI()
    {
        float horVel = VectorMath.RemoveDotVector(_controller.GetVelocity(), Vector3.up).magnitude;
        float verVel = VectorMath.ExtractDotVector(_controller.GetVelocity(), Vector3.up).magnitude;

        if (Mathf.Abs(horVel) > _topSpeedHorizontal) _topSpeedHorizontal = horVel;
        if (Mathf.Abs(verVel) > _topSpeedVertical) _topSpeedVertical = verVel;
        
        GUI.Label(new Rect(50f, 50f, 500f, 20f), $"H: {horVel:F}");
        GUI.Label(new Rect(50f, 70f, 500f, 20f), $"H^: {_topSpeedHorizontal:F}");
        GUI.Label(new Rect(50f, 90f, 500f, 20f), $"V: {verVel:F}");
        GUI.Label(new Rect(50f, 110f, 500f, 20f), $"V^: {_topSpeedVertical:F}");
        GUI.Label(new Rect(50f, 140f, 500f, 20f), _controller.GetState().GetType().ToString());
    }
}