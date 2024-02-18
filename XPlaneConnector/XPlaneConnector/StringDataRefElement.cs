namespace XPlaneConnector;

public class StringDataRefElement
{
    private static readonly object lockElement = new();
    public string DataRef { get; set; }
    public int Frequency { get; set; }
    public int StringLenght { get; set; }
    public string Value { get; set; }
    public DateTime LastUpdateTime { get; set; }
    private readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    private int _charactersInitialized;

    public bool IsCompletelyInitialized
    {
        get
        {
            return _charactersInitialized >= StringLenght;
        }
    }

    public delegate void NotifyChangeHandler(StringDataRefElement sender, string newValue);
    public event NotifyChangeHandler OnValueChange;

    public void Update(int index, char character)
    {
        lock (lockElement)
        {
            if ((DateTime.Now - LastUpdateTime) > MaxAge)
            {
                // The string has changed, this is the first character received of the new string, so we invalidate the previous string
                _charactersInitialized = 0;
                Value = "";
            }
            LastUpdateTime = DateTime.Now;

            var fireEvent = !IsCompletelyInitialized;

            if (!IsCompletelyInitialized)
            {

                _charactersInitialized++;
            }

            if (character > 0)
            {
                if (Value.Length <= index)
                {
                    Value = Value.PadRight(index + 1, ' ');
                }


                var current = Value[index];
                if (current != character)
                {
                    Value = Value.Remove(index, 1).Insert(index, character.ToString());
                    fireEvent = true;
                }
            }

            if (IsCompletelyInitialized && fireEvent)
            {
                OnValueChange?.Invoke(this, Value);
                _charactersInitialized = 0;
            }
        }
    }

    public StringDataRefElement()
    {
        _charactersInitialized = 0;
        Value = "";
    }
}
