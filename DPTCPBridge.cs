using System;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class RingBuffer
{
    public byte[] Buffer;
    int head;
    int tail;
    int size;

    public RingBuffer(int _size)
    {
        this.size = _size - 1;
        this.Buffer = new byte[_size];
    }

    public int Read()
    {
        byte temp;
        if (this.tail == this.head)
        {
            Console.WriteLine("Head == tail on read(), Head = " + this.head + " Tail = " + tail);
            return -1;
        }

        temp = this.Buffer[this.tail];

        this.tail = (this.tail + 1) % this.size;
        return temp;
    }

    public int Write(byte data)
    {
        if (((this.head + 1) % this.size) == this.tail)
        {
            Console.WriteLine("Head == tail on write(" + data + ")");
            return -1;
        }

        this.Buffer[head] = data;
        this.head = (this.head + 1) % this.size;

        return 0;
    }

    public void Reset()
    {
        head = 0;
        tail = 0;
    }
}

public class DPTCPBridge : DataPort
{
    private static Socket socket;
    public const int socketBufferSize = 1024 * 4;
    public static byte[] socketBuffer_Receive = new byte[socketBufferSize];

    public RingBuffer ringBuffer_Receive = new RingBuffer(socketBufferSize * 2);
    private volatile bool data_is_ready = false;

    private volatile int _bytestoread = 0;
    override public int BytesToRead
    {
        get
        {
            if (data_is_ready)
            {
                return _bytestoread;
            }
            else
            {
                return 0;
            }
        }
    }

    private volatile int _bytestowrite = 0;
    override public int BytesToWrite
    {
        get
        {
            return _bytestowrite;
        }
    }

    // Unused place-holders, counter-parts to serial properties used in program
    override public Handshake Handshake { get; set; }
    override public bool DtrEnable { get; set; }
    override public bool RtsEnable { get; set; }

    override public int ReadTimeout
    {
        // Consider doubling up the wait value used by nops?
        get { return socket.ReceiveTimeout; }
        set { socket.ReceiveTimeout = value; }
    }

    override public int WriteTimeout
    {
        get { return socket.SendTimeout; }
        set { socket.SendTimeout = value; }
    }

    static IPEndPoint remoteEndpoint;
    static IPAddress ip;

    public override void Open()
    {
        try
        {
            Console.WriteLine("Opening remote connection to " + ip + ":" + remoteEndpoint.Port);
            socket.Connect(remoteEndpoint);

            // Give the accepting socket time to accept the connection
            // The reset command fires off and closes the connection especially quickly
            Thread.Sleep(100);
        }
        catch (SocketException e)
        {
            Console.WriteLine(e.Message);
        }
        catch (Exception e)
        {
            Console.WriteLine("Source : " + e.Source);
            Console.WriteLine("Message : " + e.Message);
        }

        if (socket.Connected)
        {
            Console.WriteLine("Connected to " + ip + ":" + remoteEndpoint.Port);
            Console.WriteLine("Starting async receive task");
            socket.BeginReceive(socketBuffer_Receive, 0, socketBufferSize, 0, new AsyncCallback(RecieveCallback), socket);
        }
        else
        {
            throw new System.Exception("Failed to connect to " + ip + ":" + remoteEndpoint.Port);
        }
    }

    public override void Close()
    {
        socket.Close();
        this._bytestoread = 0;
        this._bytestowrite = 0;
        this.ringBuffer_Receive.Reset();
    }

    public override int ReadByte()
    {
        int temp;
        if (this._bytestoread <= 0)
        {
            Console.WriteLine("ReadByte() this._bytestoread <= 0");
            this._bytestoread = 0;
            return -1;
        }

        temp = this.ringBuffer_Receive.Read();

        if (temp < 0)
        {
            Console.WriteLine("ReadByte() temp < 0 with " + this._bytestoread + "bytes to read");
            this._bytestoread = 0;
            return 0;
        }
        else
        {
            this._bytestoread--;
            return Convert.ToByte(temp);
        }
    }

    public override int ReadChar()
    {
        int temp;
        if (this._bytestoread <= 0)
        {
            Console.WriteLine("ReadChar() this._bytestoread <= 0");
            this._bytestoread = 0;
            return -1;
        }

        temp = this.ringBuffer_Receive.Read();

        if (temp < 0)
        {
            Console.WriteLine("ReadChar() temp < 0 with " + this._bytestoread + "bytes to read");
            this._bytestoread = 0;
            return 0;
        }
        else
        {
            this._bytestoread--;
            return Convert.ToChar(temp);
        }
    }

    public override void Write(string text)
    {
        this._bytestowrite += text.Length ;
        this._bytestowrite -= socket.Send(Encoding.ASCII.GetBytes(text));
    }

    public override void Write(char[] buffer, int offset, int count)
    {
        this._bytestowrite += count;
        this._bytestowrite -= socket.Send(Encoding.ASCII.GetBytes(buffer, offset, count));
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        this._bytestowrite += count;
        this._bytestowrite -= socket.Send(buffer, offset, count, SocketFlags.None);
    }

    public DPTCPBridge(string remoteHost, UInt32 remotePort) : base(remoteHost, remotePort)
    {
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        ip = IPAddress.Parse(remoteHost);
        remoteEndpoint = new IPEndPoint(ip, (int)remotePort);
    }

    private void RecieveCallback(IAsyncResult ar)
    {
        try
        {
            Socket recvSocket = (Socket)ar.AsyncState;
            int numBytesRead = recvSocket.EndReceive(ar);
            data_is_ready = false;

            if (numBytesRead > 0)
            {
                for (int i = 0; i < numBytesRead; i++)
                {
                    if (this.ringBuffer_Receive.Write(Convert.ToByte(socketBuffer_Receive[i])) < 0)
                    {
                        Console.WriteLine("buffer.Write() < 0");
                        return;
                    }
                    else
                    {
                        this._bytestoread++;
                    }
                }
                data_is_ready = true;
                recvSocket.BeginReceive(socketBuffer_Receive, 0, socketBufferSize, 0, new AsyncCallback(RecieveCallback), recvSocket);
            }
            else
            {
                Console.WriteLine("Read 0 bytes...");
            }
        }
        catch (ObjectDisposedException e)
        {

        }
        catch (Exception e)
        {
            Console.WriteLine("Source : " + e.Source);
            Console.WriteLine("Message : " + e.Message);
        }
    }
}
