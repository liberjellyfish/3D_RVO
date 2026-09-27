namespace Rvo
{
    // 无资源模块共用生命周期；有 Native 容器的模块自行实现 IDisposable。
    public abstract class StatelessModule : ISimulationModule
    {
        public abstract string Name { get; }
        public bool IsImplemented => true;
        public void Dispose() { }
    }
}
