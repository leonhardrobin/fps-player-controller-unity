using UnityUtils.StateMachine;

public class GroundedState : IState
{
    private readonly PlayerController _controller;

    public GroundedState(PlayerController controller)
    {
        _controller = controller;
    }

    public void OnEnter()
    {
        _controller.OnGroundContactRegained();
    }
}

public class FallingState : IState
{
    private readonly PlayerController _controller;

    public FallingState(PlayerController controller)
    {
        _controller = controller;
    }

    public void OnEnter()
    {
        _controller.OnFallStart();
    }
}

public class SlidingState : IState
{
    private readonly PlayerController _controller;

    public SlidingState(PlayerController controller)
    {
        _controller = controller;
    }

    public void OnEnter()
    {
        _controller.OnGroundContactLost();
    }
}

public class RisingState : IState
{
    private readonly PlayerController _controller;

    public RisingState(PlayerController controller)
    {
        _controller = controller;
    }

    public void OnEnter()
    {
        _controller.OnGroundContactLost();
    }
}

public class JumpingState : IState
{
    private readonly PlayerController _controller;

    public JumpingState(PlayerController controller)
    {
        _controller = controller;
    }

    public void OnEnter()
    {
        _controller.OnGroundContactLost();
        _controller.OnJumpStart();
    }
}