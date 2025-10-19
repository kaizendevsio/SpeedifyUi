namespace XNetwork.Utils;

/// <summary>
/// Thread-safe fixed-size circular buffer for rolling window storage
/// </summary>
/// <typeparam name="T">Type of items to store</typeparam>
public class CircularBuffer<T>
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private readonly object _lock = new object();
    private int _head; // Points to next write position
    private int _count; // Current number of items

    /// <summary>
    /// Creates a new circular buffer with the specified capacity
    /// </summary>
    /// <param name="capacity">Maximum number of items to store</param>
    public CircularBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentException("Capacity must be greater than zero", nameof(capacity));

        _capacity = capacity;
        _buffer = new T[capacity];
        _head = 0;
        _count = 0;
    }

    /// <summary>
    /// Gets the current number of items in the buffer
    /// </summary>
    public int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>
    /// Gets the maximum capacity of the buffer
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Adds an item to the buffer, overwriting the oldest item if full
    /// </summary>
    /// <param name="item">Item to add</param>
    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _capacity;
            
            if (_count < _capacity)
                _count++;
        }
    }

    /// <summary>
    /// Gets all items in the buffer in chronological order (oldest to newest)
    /// </summary>
    /// <returns>Array of items</returns>
    public T[] GetItems()
    {
        lock (_lock)
        {
            if (_count == 0)
                return Array.Empty<T>();

            var items = new T[_count];
            
            if (_count < _capacity)
            {
                // Buffer not full yet - items are from index 0 to _count-1
                Array.Copy(_buffer, 0, items, 0, _count);
            }
            else
            {
                // Buffer is full - items wrap around
                var firstPartLength = _capacity - _head;
                Array.Copy(_buffer, _head, items, 0, firstPartLength);
                Array.Copy(_buffer, 0, items, firstPartLength, _head);
            }
            
            return items;
        }
    }

    /// <summary>
    /// Clears all items from the buffer
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer, 0, _capacity);
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// Gets a snapshot of buffer statistics
    /// </summary>
    /// <returns>Tuple of (count, capacity)</returns>
    public (int count, int capacity) GetStats()
    {
        lock (_lock)
        {
            return (_count, _capacity);
        }
    }
}