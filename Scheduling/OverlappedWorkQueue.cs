using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Scheduling
{
    internal class OverlappedWorkQueue
    {
        private readonly ConcurrentQueue<Work> _workItems = new ConcurrentQueue<Work>();
        private int _doingWork;
        private ScheduledOverlapped _overlapped;

        public OverlappedWorkQueue()
        {
            _overlapped = new ScheduledOverlapped(this);
        }

        public void Schedule(Action<object> action, object state)
        {
            _workItems.Enqueue(new Work(action, state));

            // Set working if it wasn't (via atomic Interlocked).
            if (Interlocked.CompareExchange(ref _doingWork, 1, 0) == 0)
            {
                // Wasn't working, schedule.
                _overlapped.Post();
            }
        }

        void Execute()
        {
            while (true)
            {
                while (_workItems.TryDequeue(out Work item))
                {
                    item.Callback(item.State);
                }

                // All work done.

                // Set _doingWork (0 == false) prior to checking IsEmpty to catch any missed work in interim.
                // This doesn't need to be volatile due to the following barrier (i.e. it is volatile).
                _doingWork = 0;

                // Ensure _doingWork is written before IsEmpty is read.
                // As they are two different memory locations, we insert a barrier to guarantee ordering.
                Thread.MemoryBarrier();

                // Check if there is work to do
                if (_workItems.IsEmpty)
                {
                    // Nothing to do, exit.
                    break;
                }

                // Is work, can we set it as active again (via atomic Interlocked), prior to scheduling?
                if (Interlocked.Exchange(ref _doingWork, 1) == 1)
                {
                    // Execute has been rescheduled already, exit.
                    break;
                }

                // Is work, wasn't already scheduled so continue loop.
            }
        }

        private readonly struct Work
        {
            public readonly Action<object> Callback;
            public readonly object State;

            public Work(Action<object> callback, object state)
            {
                Callback = callback;
                State = state;
            }
        }

        private unsafe class ScheduledOverlapped : Overlapped
        {
            private readonly NativeOverlapped* _nativeOverlapped;
            private readonly OverlappedWorkQueue _scheduler;

            public ScheduledOverlapped(OverlappedWorkQueue scheduler)
            {
                _scheduler = scheduler;
                _nativeOverlapped = Pack(IOCallback, null);
            }

            void IOCallback(uint errorCode, uint numBytes, NativeOverlapped* nativeOverlapped)
            {
                _scheduler.Execute();
            }

            public void Post()
            {
                ThreadPool.UnsafeQueueNativeOverlapped(_nativeOverlapped);
            }

            public void Cleanup()
            {
                Free(_nativeOverlapped);
            }
        }
    }
}
