using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XPlaneConnector.Core;

/// <summary>
/// Constructor
/// </summary>
/// <param name="ip">IP of the machine running X-Plane, default 127.0.0.1 (localhost)</param>
/// <param name="xplanePort">Port the machine running X-Plane is listening for, default 49000</param>
public class Connector(string ip = "127.0.0.1", int xplanePort = 49000) : IDisposable
{
    private const int CheckInterval_ms = 1000;
    private readonly TimeSpan _maxDataRefAge = TimeSpan.FromSeconds(5);

    private readonly CultureInfo _enCulture = new("en-US");

    private UdpClient _server;
    private UdpClient _client;
    private readonly IPEndPoint _xPlaneEP = new(IPAddress.Parse(ip), xplanePort);
    private CancellationTokenSource _ts;
    private Task _serverTask;
    private Task _observerTask;

    public delegate void RawReceiveHandler(string raw);
    public event RawReceiveHandler OnRawReceive;

    public delegate void DataRefReceived(DataRefElement dataRef);
    public event DataRefReceived OnDataRefReceived;

    public delegate void LogHandler(string message);
    public event LogHandler OnLog;

    private readonly List<DataRefElement> _dataRefs = [];

    public DateTime LastReceive { get; internal set; }
    public IEnumerable<byte> LastBuffer { get; internal set; }
    public IPEndPoint LocalEP
    {
        get
        {
            return (IPEndPoint)_client.Client.LocalEndPoint;
        }
    }

    /// <summary>
    /// Initialize the communication with X-Plane machine and starts listening for DataRefs
    /// </summary>
    public void Start()
    {
        _client = new UdpClient();
        _client.Connect(_xPlaneEP.Address, _xPlaneEP.Port);

        _server = new UdpClient(LocalEP);

        _ts = new CancellationTokenSource();
        var token = _ts.Token;

        _serverTask = Task.Factory.StartNew(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var response = await _server.ReceiveAsync().ConfigureAwait(false);
                var raw = Encoding.UTF8.GetString(response.Buffer);
                LastReceive = DateTime.Now;
                LastBuffer = response.Buffer;

                OnRawReceive?.Invoke(raw);
                ParseResponse(response.Buffer);
            }

            OnLog?.Invoke("Stopping server");
            _server.Close();
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        _observerTask = Task.Factory.StartNew(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                foreach (var dr in _dataRefs)
                {
                    if (dr.Age > _maxDataRefAge)
                    {
                        RequestDataRef(dr);
                    }
                }

                await Task.Delay(CheckInterval_ms).ConfigureAwait(false);
            }

        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>
    /// Stops the comunications with the X-Plane machine
    /// </summary>
    /// <param name="timeout"></param>
    public void Stop(int timeout = 5000)
    {
        if (_client != null)
        {
            var localDataRefs = _dataRefs.ToArray();
            foreach (var dr in localDataRefs)
            {
                Unsubscribe(dr.DataRef);
            }

            if (_ts != null)
            {
                _ts.Cancel();
                Task.WaitAll([_serverTask, _observerTask], timeout);
                _ts.Dispose();
                _ts = null;

                _client.Close();
            }
        }
    }
    private void ParseResponse(byte[] buffer)
    {
        var pos = 0;
        var header = Encoding.UTF8.GetString(buffer, pos, 4);
        pos += 5; // Including tailing 0

        if (header == "RREF") // Ignore other messages
        {
            while (pos < buffer.Length)
            {
                var id = BitConverter.ToInt32(buffer, pos);
                pos += 4;

                try
                {
                    var value = BitConverter.ToSingle(buffer, pos);
                    pos += 4;
                    var localDataRefs = _dataRefs.ToArray();
                    foreach (var dr in localDataRefs)
                    {
                        if (dr.Update(id, value))
                        {
                            OnDataRefReceived?.Invoke(dr);
                        }
                    }
                }
                catch (ArgumentException ex)
                {

                }
                catch (Exception ex)
                {
                    var error = ex.Message;
                }
            }
        }
    }

    /// <summary>
    /// Sends a command
    /// </summary>
    /// <param name="command">Command to send</param>
    public void SendCommand(XPlaneCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dg = new XPDatagram();
        dg.Add("CMND");
        dg.Add(command.Command);

        _client.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Sends a command continously. Use return parameter to cancel the send cycle
    /// </summary>
    /// <param name="command">Command to send</param>
    /// <returns>Token to cancel the executing</returns>
    public CancellationTokenSource StartCommand(XPlaneCommand command)
    {
        var tokenSource = new CancellationTokenSource();

        Task.Run(() =>
        {
            while (!tokenSource.IsCancellationRequested)
            {
                SendCommand(command);
            }
        }, tokenSource.Token);

        return tokenSource;
    }

    public static void StopCommand(CancellationTokenSource token)
    {
        token.Cancel();
    }

    /// <summary>
    /// Subscribe to a DataRef, notification will be sent every time the value changes
    /// </summary>
    /// <param name="dataref">DataRef to subscribe to</param>
    /// <param name="frequency">Times per seconds X-Plane will be seding this value</param>
    /// <param name="onchange">Callback invoked every time a change in the value is detected</param>
    public void Subscribe(DataRefElement dataref, int frequency = -1, Action<DataRefElement, float> onchange = null)
    {
        ArgumentNullException.ThrowIfNull(dataref);

        if (onchange != null)
        {
            dataref.OnValueChange += (e, v) => onchange(e, v);
        }

        if (frequency > 0)
        {
            dataref.Frequency = frequency;
        }

        _dataRefs.Add(dataref);
    }

    /// <summary>
    /// Subscribe to a DataRef, notification will be sent every time the value changes
    /// </summary>
    /// <param name="dataref">DataRef to subscribe to</param>
    /// <param name="frequency">Times per seconds X-Plane will be seding this value</param>
    /// <param name="onchange">Callback invoked every time a change in the value is detected</param>
    public void Subscribe(StringDataRefElement dataref, int frequency = -1, Action<StringDataRefElement, string> onchange = null)
    {
        //if (onchange != null)
        //    dataref.OnValueChange += (e, v) => { onchange(e, v); };

        //Subscribe((DataRefElement)dataref, frequency);

        ArgumentNullException.ThrowIfNull(dataref);

        dataref.OnValueChange += (e, v) => onchange(e, v);

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
        if (_client != null)
        {
            var dg = new XPDatagram();
            dg.Add("RREF");
            dg.Add(element.Frequency);
            dg.Add(element.Id);
            dg.Add(element.DataRef);
            dg.FillTo(413);

            _client.Send(dg.Get(), dg.Len);

            OnLog?.Invoke($"Requested {element.DataRef}@{element.Frequency}Hz with Id:{element.Id}");
        }
    }

    /// <summary>
    /// Informs X-Plane to stop sending this DataRef
    /// </summary>
    /// <param name="dataref">DataRef to unsubscribe to</param>
    public void Unsubscribe(string dataref)
    {
        var dr_list = _dataRefs.Where(d => d.DataRef == dataref).ToArray();

        foreach (var dr in dr_list)
        {
            var dg = new XPDatagram();
            dg.Add("RREF");
            dg.Add(dr.Id);
            dg.Add(0);
            dg.Add(dataref);
            dg.FillTo(413);

            _client.Send(dg.Get(), dg.Len);
            _dataRefs.Remove(dr);

            OnLog?.Invoke($"Unsubscribed from {dataref}");
        }
    }

    /// <summary>
    /// Informs X-Plane to change the value of the DataRef
    /// </summary>
    /// <param name="dataref">DataRef that will be changed</param>
    /// <param name="value">New value of the DataRef</param>
    public void SetDataRefValue(DataRefElement dataref, float value)
    {
        ArgumentNullException.ThrowIfNull(dataref);

        SetDataRefValue(dataref.DataRef, value);
    }

    /// <summary>
    /// Informs X-Plane to change the value of the DataRef
    /// </summary>
    /// <param name="dataref">DataRef that will be changed</param>
    /// <param name="value">New value of the DataRef</param>
    public void SetDataRefValue(string dataref, float value)
    {
        var dg = new XPDatagram();
        dg.Add("DREF");
        dg.Add(value);
        dg.Add(dataref);
        dg.FillTo(509);

        _client.Send(dg.Get(), dg.Len);
    }
    /// <summary>
    /// Informs X-Plane to change the value of the DataRef
    /// </summary>
    /// <param name="dataref">DataRef that will be changed</param>
    /// <param name="value">New value of the DataRef</param>
    public void SetDataRefValue(string dataref, string value)
    {
        var dg = new XPDatagram();
        dg.Add("DREF");
        dg.Add(value);
        dg.Add(dataref);
        dg.FillTo(509);

        _client.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Request X-Plane to close, a notification message will appear
    /// </summary>
    public void QuitXPlane()
    {
        var dg = new XPDatagram();
        dg.Add("QUIT");

        _client.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Inform X-Plane that a system is failed
    /// </summary>
    /// <param name="system">Integer value representing the system to fail</param>
    public void Fail(int system)
    {
        var dg = new XPDatagram();
        dg.Add("FAIL");

        dg.Add(system.ToString(_enCulture));

        _client.Send(dg.Get(), dg.Len);
    }

    /// <summary>
    /// Inform X-Plane that a system is back to normal functioning
    /// </summary>
    /// <param name="system">Integer value representing the system to recover</param>
    public void Recover(int system)
    {
        var dg = new XPDatagram();
        dg.Add("RECO");

        dg.Add(system.ToString(_enCulture));

        _client.Send(dg.Get(), dg.Len);
    }

    protected virtual void Dispose(bool a)
    {
        _server?.Dispose();
        _client?.Dispose();
        _ts?.Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
