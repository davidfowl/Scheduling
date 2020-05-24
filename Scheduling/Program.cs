using System;
using System.Diagnostics.Tracing;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace Scheduling
{
    class Program
    {
        static void Main(string[] args)
        {
            var overlappedWorkQueue = new OverlappedWorkQueue();
            var threadPoolWorkQueue = new ThreadPoolWorkQueue();

            var service = new Service();

            static void WorkItem(object state)
            {
                ((Service)state).Invoke();
            }

            // Cache the delegate
            Action<object> workItem = WorkItem;
            Action workItemAction = service.Invoke;

            while (true)
            {
                for (int i = 0; i < Environment.ProcessorCount; i++)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(workItem, service, preferLocal: false);
                    // IOThreadScheduler.ScheduleCallbackNoFlow(workItem, service);
                    // overlappedWorkQueue.Schedule(workItem, service);
                    // threadPoolWorkQueue.Schedule(workItem, service);
                    // Task.Run(workItemAction);
                }
            }
        }
    }

    public class ServiceEventSource : EventSource
    {
        public static ServiceEventSource Log = new ServiceEventSource();
        private IncrementingPollingCounter _invocationRateCounter;

        public int _invocations;

        public ServiceEventSource() : base("MyApp")
        {

        }

        protected override void OnEventCommand(EventCommandEventArgs command)
        {
            if (command.Command == EventCommand.Enable)
            {
                _invocationRateCounter = new IncrementingPollingCounter("invocations-rate", this, () => _invocations)
                {
                    DisplayRateTimeScale = TimeSpan.FromSeconds(1)
                };
            }
        }

        internal void Invoke()
        {
            Interlocked.Increment(ref _invocations);
        }
    }

    public class Service
    {
        public void Invoke()
        {
            ServiceEventSource.Log.Invoke();
        }
    }
}
