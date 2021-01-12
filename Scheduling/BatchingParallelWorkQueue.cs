using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Scheduling
{
    public class BatchingParallelWorkQueue : IThreadPoolWorkItem
    {
        private int _eventQueueProcessingRequested;

        private readonly ConcurrentQueue<Work> _eventQueue = new();

        public void Schedule(Action<object> action, object state)
        {
            _eventQueue.Enqueue(new Work(action, state));

            // Set working if it wasn't (via atomic Interlocked).
            ScheduleToProcessEvents();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ScheduleToProcessEvents()
        {
            // Schedule a thread pool work item to process events. Only one work item is scheduled at any given time to avoid
            // over-parallelization. When the work item begins running, this field is reset to 0, allowing for another work item
            // to be scheduled for parallelizing processing of events.
            if (Interlocked.CompareExchange(ref _eventQueueProcessingRequested, 1, 0) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            // Indicate that a work item is no longer scheduled to process events. The change needs to be visible to enqueuer
            // threads (only for EventLoop() currently) before an event is attempted to be dequeued. In particular, if an
            // enqueuer queues an event and does not schedule a work item because it is already scheduled, and this thread is
            // the last thread processing events, it must see the event queued by the enqueuer.
            Interlocked.Exchange(ref _eventQueueProcessingRequested, 0);

            ConcurrentQueue<Work> eventQueue = _eventQueue;
            if (!eventQueue.TryDequeue(out Work ev))
            {
                return;
            }

            int startTimeMs = Environment.TickCount;

            // An event was successfully dequeued, and there may be more events to process. Schedule a work item to parallelize
            // processing of events, before processing more events. Following this, it is the responsibility of the new work
            // item and the epoll thread to schedule more work items as necessary. The parallelization may be necessary here if
            // the user callback as part of handling the event blocks for some reason that may have a dependency on other queued
            // socket events.
            ScheduleToProcessEvents();

            while (true)
            {
                ev.Callback(ev.State);

                // If there is a constant stream of new events, and/or if user callbacks take long to process an event, this
                // work item may run for a long time. If work items of this type are using up all of the thread pool threads,
                // collectively they may starve other types of work items from running. Before dequeuing and processing another
                // event, check the elapsed time since the start of the work item and yield the thread after some time has
                // elapsed to allow the thread pool to run other work items.
                //
                // The threshold chosen below was based on trying various thresholds and in trying to keep the latency of
                // running another work item low when these work items are using up all of the thread pool worker threads. In
                // such cases, the latency would be something like threshold / proc count. Smaller thresholds were tried and
                // using Stopwatch instead (like 1 ms, 5 ms, etc.), from quick tests they appeared to have a slightly greater
                // impact on throughput compared to the threshold chosen below, though it is slight enough that it may not
                // matter much. Higher thresholds didn't seem to have any noticeable effect.
                if (Environment.TickCount - startTimeMs >= 15)
                {
                    break;
                }

                if (!eventQueue.TryDequeue(out ev))
                {
                    return;
                }
            }

            // The queue was not observed to be empty, schedule another work item before yielding the thread
            ScheduleToProcessEvents();
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
    }
}
