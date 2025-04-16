using System.Reflection;

public interface IAltruistEngine
{
    public CycleRate Rate { get; }

    public bool Enabled { get; }

    void Enable();

    void Disable();

    void Start();
    void Stop();

    void ScheduleTask(Delegate taskDelegate, CycleRate? frequencyHz = null);
    void SendTask(TaskIdentifier taskId, Delegate taskDelegate);
    void RegisterCronJob(Delegate jobDelegate, string cronExpression, object? serviceInstance = null);
}