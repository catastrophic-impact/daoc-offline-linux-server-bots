using System.Collections.Generic;

namespace DOL.GS;

// Accessed under the porter's batch lock. Three priority passengers followed
// by one ordinary passenger prevents a busy defense from starving departures.
public sealed class FrontierBoardingQueue<T> where T : class
{
    private readonly Queue<T> _priority = new();
    private readonly Queue<T> _ordinary = new();
    private readonly HashSet<T> _queuedPriority = new();
    private int _priorityRun;
    public int OrdinaryCount => _ordinary.Count;
    public int Count => _ordinary.Count + _priority.Count;
    public void Enqueue(T item) => _ordinary.Enqueue(item);
    public void EnqueuePriority(T item)
    {
        if (_queuedPriority.Add(item)) _priority.Enqueue(item);
    }
    public bool TryDequeue(out T item, out bool priority)
    {
        priority = _priority.Count > 0 && (_ordinary.Count == 0 || _priorityRun < 3);
        if (priority)
        {
            item = _priority.Dequeue(); _queuedPriority.Remove(item);
            _priorityRun++; return true;
        }
        _priorityRun = 0;
        return _ordinary.TryDequeue(out item);
    }
    public void Clear()
    {
        _priority.Clear(); _ordinary.Clear(); _queuedPriority.Clear(); _priorityRun = 0;
    }
}
