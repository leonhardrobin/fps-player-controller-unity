using UnityEngine;
using UnityUtils;

public class ControllerDebugger : MonoBehaviour
{
    [SerializeField] private PlayerController _controller;

    private void OnGUI()
    {
        string horVelString = VectorMath.RemoveDotVector(_controller.GetVelocity(), Vector3.up).magnitude.ToString("F");
        string verVelString = VectorMath.ExtractDotVector(_controller.GetVelocity(), Vector3.up).magnitude.ToString("F");
        GUI.Label(new Rect(50f, 50f, 200f, 20f), $"H: {horVelString}");
        GUI.Label(new Rect(50f, 80f, 200f, 20f), $"V: {verVelString}");
    }
}