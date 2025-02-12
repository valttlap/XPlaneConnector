using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XPlaneConnector.Core;

/// <summary>
/// Connector to X-Plane via UDP
/// </summary>
/// <remarks>
/// Constructor
/// </remarks>
/// <param name="ip">IP of the machine running X-Plane, default 127.0.0.1 (localhost)</param>
/// <param name="xplanePort">Port the machine running X-Plane is listening on, default 49000</param>
public class Connector(IPEndPoint? xPlaneEndPoint) : IDisposable
{
    private const int CheckIntervalMs = 1000;
    private readonly TimeSpan _maxDataRefAge = TimeSpan.FromSeconds(5);

    private readonly CultureInfo _enCulture = new("en-US");

    private UdpClient? _server;
    private UdpClient? _client;
    private readonly IPEndPoint _xplaneEndPoint = xPlaneEndPoint is null ? new IPEndPoint(IPAddress.Parse("127.0.0.1"), 49000) : xPlaneEndPoint;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private Task? _observerTask;

    // Track disposal to prevent usage after disposal
    private bool _disposed = false;

    public event Action<Exception>? ServerFailed;
    public event Action<string>? OnRawReceive;
    public event Action<DataRefElement>? OnDataRefReceived;
    public event Action<string>? OnLog;

    private readonly List<DataRefElement> _dataRefs = [];
    private readonly object _dataRefsLock = new();

    public DateTime LastReceive { get; internal set; } = DateTime.MinValue;
    public IEnumerable<byte> LastBuffer { get; internal set; } = [];

    public IPEndPoint? LocalEP => (IPEndPoint?)_client?.Client?.LocalEndPoint;


    /// <summary>
    /// Start listening and communicating with X-Plane
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_client is not null || _server is not null)
        {
            throw new InvalidOperationException("Connector is already started.");
        }

        _client = new UdpClient();
        _client.Connect(_xplaneEndPoint);

        if (LocalEP is null)
        {
            throw new InvalidOperationException("Local endpoint is null");
        }

        _server = new UdpClient(LocalEP);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Server Task
        _serverTask = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    UdpReceiveResult response;
                    try
                    {
                        response = await _server.ReceiveAsync().ConfigureAwait(false);
                    }
                    catch (SocketException ex)
                    {
                        OnLog?.Invoke($"SocketException: {ex.Message}");
                        ServerFailed?.Invoke(ex);
                        continue;
                    }

                    LastReceive = DateTime.Now;
                    LastBuffer = response.Buffer;
                    var raw = Encoding.UTF8.GetString(response.Buffer);

                    OnRawReceive?.Invoke(raw);
                    ParseResponse(response.Buffer);
                }
            }
            catch (ObjectDisposedException)
            {
                // UdpClient was closed, safe to ignore
            }
            catch (Exception ex)
            {
                OnLog?.Invoke("Unhandled exception in serverTask: " + ex);
            }
            finally
            {
                OnLog?.Invoke("Stopping server");
                _server.Close();
            }
        }, token);

        // Observer Task
        _observerTask = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    lock (_dataRefsLock)
                    {
                        foreach (var dr in _dataRefs)
                        {
                            if (dr.Age > _maxDataRefAge)
                            {
                                RequestDataRef(dr);
                            }
                        }
                    }
                    await Task.Delay(CheckIntervalMs, token).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException)
            {
                // expected when token is canceled
            }
            catch (Exception ex)
            {
                OnLog?.Invoke("Unhandled exception in observerTask: " + ex);
            }
        }, token);
    }

    /// <summary>
    /// Stop the communications with the X-Plane machine
    /// </summary>
    /// <param name="timeout">Timeout in milliseconds to wait for tasks to complete</param>
    public void Stop(int timeout = 5000)
    {
        if (_cts == null) return; // not started or already stopped

        try
        {
            _cts.Cancel();

            // Close server to unblock ReceiveAsync
            _server?.Close();

            // Close client as well
            _client?.Close();

            var tasks = new[] { _serverTask, _observerTask }
                .Where(t => t is not null)
                .ToArray();

            if (tasks.Length > 0)
            {
                Task.WaitAll(tasks!, timeout);
            }
            // Wait for tasks to finish
        }
        catch (AggregateException agex)
        {
            foreach (var ex in agex.InnerExceptions)
            {
                OnLog?.Invoke("Exception during Stop(): " + ex.Message);
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;

            _server = null;
            _client = null;
            _serverTask = null;
            _observerTask = null;
        }
    }

    private void ParseResponse(byte[] buffer)
    {
        var pos = 0;
        if (buffer.Length < 5) return;

        var header = Encoding.UTF8.GetString(buffer, pos, 4);
        if (header != "RREF") return; // Ignore other messages

        pos += 5; // including trailing '\0'

        // Each entry: [int id (4 bytes)] + [float value (4 bytes)]
        while (pos + 8 <= buffer.Length)
        {
            var id = BitConverter.ToInt32(buffer, pos);
            pos += 4;

            var value = BitConverter.ToSingle(buffer, pos);
            pos += 4;

            DataRefElement[] localDataRefs;
            lock (_dataRefsLock)
            {
                // create a copy to avoid enumerating a list being modified
                localDataRefs = _dataRefs.ToArray();
            }

            foreach (var dr in localDataRefs)
            {
                if (dr.Update(id, value))
                {
                    OnDataRefReceived?.Invoke(dr);
                }
            }
        }
    }

    /// <summary>
    /// Sends a one-shot command to X-Plane.
    /// </summary>
    /// <param name="command">Command to send</param>
    public void SendCommand(XPlaneCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);

        var dg = new XPDatagram();
        dg.Add("CMND");
        dg.Add(command.Command);

        _client?.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Sends a command continuously on a background task. Use the returned <see cref="CancellationTokenSource"/> to stop.
    /// </summary>
    /// <param name="command">Command to send</param>
    /// <returns>A token source used to stop sending the command</returns>
    public CancellationTokenSource StartCommand(XPlaneCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);

        var tokenSource = new CancellationTokenSource();
        var token = tokenSource.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                SendCommand(command);
                // Without a delay, this loop would be CPU-intensive and spam X-Plane
                await Task.Delay(10, token).ConfigureAwait(false);
            }
        }, token);

        return tokenSource;
    }

    /// <summary>
    /// Stops a command cycle started by <see cref="StartCommand"/>
    /// </summary>
    /// <param name="token">The token source returned by StartCommand</param>
    public static void StopCommand(CancellationTokenSource token)
    {
        token?.Cancel();
    }

    /// <summary>
    /// Subscribe to a DataRef, notification will be sent every time the value changes
    /// </summary>
    /// <param name="dataref">DataRef to subscribe to</param>
    /// <param name="frequency">Times per second X-Plane will send this value</param>
    /// <param name="onchange">Callback invoked every time a change in the value is detected</param>
    public void Subscribe(DataRefElement dataref, int frequency = -1, Action<DataRefElement, float>? onchange = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dataref);

        if (onchange != null)
        {
            dataref.OnValueChange += (e, v) => onchange(e, v);
        }

        if (frequency > 0)
        {
            dataref.Frequency = frequency;
        }

        lock (_dataRefsLock)
        {
            _dataRefs.Add(dataref);
        }
    }

    /// <summary>
    /// Subscribe to a string-based DataRef, notification sent every time the value changes
    /// </summary>
    /// <param name="dataref">StringDataRefElement to subscribe to</param>
    /// <param name="frequency">Times per second X-Plane will send this value</param>
    /// <param name="onchange">Callback invoked on every change</param>
    public void Subscribe(StringDataRefElement dataref, int frequency = -1, Action<StringDataRefElement, string>? onchange = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dataref);

        dataref.OnValueChange += (e, v) => onchange?.Invoke(e, v);

        // Each character of the string is stored as an array of single-character DataRefElements
        for (var c = 0; c < dataref.StringLenght; c++)
        {
            var arrayElementDataRef = new DataRefElement
            {
                DataRef = $"{dataref.DataRef}[{c}]",
                Description = ""
            };

            var currentIndex = c;
            Subscribe(arrayElementDataRef, frequency, (e, v) =>
            {
                var character = Convert.ToChar(Convert.ToInt32(v));
                dataref.Update(currentIndex, character);
            });
        }
    }

    private void RequestDataRef(DataRefElement element)
    {
        if (_client == null) return;

        var dg = new XPDatagram();
        dg.Add("RREF");
        dg.Add(element.Frequency);
        dg.Add(element.Id);
        dg.Add(element.DataRef);
        dg.FillTo(413);

        _client.Send(dg.Get(), dg.Len);

        OnLog?.Invoke($"Requested {element.DataRef}@{element.Frequency}Hz with Id:{element.Id}");
    }

    /// <summary>
    /// Informs X-Plane to stop sending this DataRef
    /// </summary>
    /// <param name="dataref">DataRef to unsubscribe from</param>
    public void Unsubscribe(string dataref)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client == null) return;

        DataRefElement[] drList;
        lock (_dataRefsLock)
        {
            drList = _dataRefs.Where(d => d.DataRef == dataref).ToArray();
        }

        foreach (var dr in drList)
        {
            var dg = new XPDatagram();
            dg.Add("RREF");
            dg.Add(dr.Id);
            dg.Add(0);
            dg.Add(dataref);
            dg.FillTo(413);

            _client.Send(dg.Get(), dg.Len);

            lock (_dataRefsLock)
            {
                _dataRefs.Remove(dr);
            }

            OnLog?.Invoke($"Unsubscribed from {dataref}");
        }
    }

    /// <summary>
    /// Informs X-Plane to change the value of the DataRef
    /// </summary>
    public void SetDataRefValue(DataRefElement dataref, float value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dataref);

        SetDataRefValue(dataref.DataRef, value);
    }

    /// <summary>
    /// Informs X-Plane to change the value of the DataRef
    /// </summary>
    public void SetDataRefValue(string dataref, float value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var dg = new XPDatagram();
        dg.Add("DREF");
        dg.Add(value);
        dg.Add(dataref);
        dg.FillTo(509);

        _client?.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Informs X-Plane to change the value of the DataRef using a string payload
    /// </summary>
    public void SetDataRefValue(string dataref, string value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var dg = new XPDatagram();
        dg.Add("DREF");
        dg.Add(value);
        dg.Add(dataref);
        dg.FillTo(509);

        _client?.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Request X-Plane to close, showing a notification in the simulator
    /// </summary>
    public void QuitXPlane()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var dg = new XPDatagram();
        dg.Add("QUIT");
        _client?.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Informs X-Plane that a system should fail
    /// </summary>
    /// <param name="system">Integer value representing the system to fail</param>
    public void Fail(int system)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var dg = new XPDatagram();
        dg.Add("FAIL");
        dg.Add(system.ToString(_enCulture));

        _client?.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Informs X-Plane that a system has recovered
    /// </summary>
    /// <param name="system">Integer value representing the system to recover</param>
    public void Recover(int system)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var dg = new XPDatagram();
        dg.Add("RECO");
        dg.Add(system.ToString(_enCulture));

        _client?.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Dispose of the connector, stopping any running tasks.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // Free managed resources
            Stop();

            _server?.Dispose();
            _server = null;

            _client?.Dispose();
            _client = null;

            _cts?.Dispose();
            _cts = null;
        }

        // If you had unmanaged resources, free them here.

        _disposed = true;
    }

}
