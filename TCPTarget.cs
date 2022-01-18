//
// For targeting a TCP client connnected to the serial port.
//
// E.g. an ESP32 on wifi, which pushes the raw bytes back
// and forth through the serial port.
//
// E.g. nops in TCP<->Serial bridge mode on another machine
//
// E.g. an emulator with a local port open which speaks nops
//

using System;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class RingBuffer
{
    // Adapted from https://gist.github.com/ryankurte/61f95dc71133561ed055ff62b33585f8
    public byte[] Buffer;
    uint head;
    uint tail;
    uint size;

    public RingBuffer(uint _size)
    {
        this.size = _size;
        this.Buffer = new byte[_size];
    }

    public long BytesAvailable()
    {
        return Math.Abs(this.head - this.tail);
    }

    public void Discard() {
        this.head = 0;
        this.tail = 0;
    }

    public int Read()
    {
        byte temp;
        if (this.tail == this.head)
        {
            // Empty - bytes not written?
            Console.WriteLine("Underflow condition tail == head on read(), Head = " + this.head + " Tail = " + tail);
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
            // Buffer full
            Console.WriteLine("Overflow conditon, Head == tail on write(" + data + ")");
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

public class TCPTarget : TargetDataPort
{
    private static Socket socket;
    public const int socketBufferSize = 1024 * 2;
    public const int ringBufferSize = 1024 * 4;
    public static byte[] socketBuffer_Receive = new byte[socketBufferSize];

    public RingBuffer ringBuffer_Receive = new RingBuffer(ringBufferSize);

    override public int BytesToRead
    {
        get
        {
            return (int)ringBuffer_Receive.BytesAvailable();
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
        get { return socket.ReceiveTimeout; }
        // To-do: Move this hacky fix to the port creation
        set { socket.ReceiveTimeout = value * 2; }
    }

    override public int WriteTimeout
    {
        get { return socket.SendTimeout; }
        // To-do: Move this hacky fix to the port creation
        set { socket.SendTimeout = value * 2; }
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
        // Try to give the tcp thread some time to cleanly finish transmission
        // The reset command fires off and closes the connection especially quickly
        Thread.Sleep( 100 );

        socket.Close();
        this._bytestowrite = 0;
        this.ringBuffer_Receive.Reset();
    }

    public override int ReadByte()
    {
        int temp = 0;

        if (this.ringBuffer_Receive.BytesAvailable() == 0)
        {
            DateTime delay_start_time = DateTime.Now;
            DateTime delay_end_time = delay_start_time.AddMilliseconds(this.ReadTimeout);

            while (this.ringBuffer_Receive.BytesAvailable() == 0)
            {
                if (DateTime.Now >= delay_end_time)
                {
                    Console.WriteLine("ReadByte() timeout\n");
                    return -1;
                }
            }
        }

        temp = this.ringBuffer_Receive.Read();

        if (temp < 0)
        {
            Console.WriteLine("ReadByte() temp < 0 with " + this.ringBuffer_Receive.BytesAvailable() + "bytes to read");
            this.ringBuffer_Receive.Discard(); // Program closes after this but might as well tidy up
            return 0;
        }
        else
        {
            return Convert.ToByte(temp);
        }
    }

    public override int ReadChar()
    {
        int temp = 0;

        if ( this.ringBuffer_Receive.BytesAvailable() == 0)
        {
            DateTime delay_start_time = DateTime.Now;
            DateTime delay_end_time = delay_start_time.AddMilliseconds(this.ReadTimeout);

            while ( this.ringBuffer_Receive.BytesAvailable() == 0)
            {
                if (DateTime.Now >= delay_end_time)
                {
                    Console.WriteLine("ReadChar() timeout\n");
                    return -1;
                }
            }
        }

        temp = this.ringBuffer_Receive.Read();

        if (temp < 0)
        {
            Console.WriteLine("ReadChar() temp < 0 with " + this.ringBuffer_Receive.BytesAvailable() + "bytes to read");
            this.ringBuffer_Receive.Discard(); // Program closes after this but might as well tidy up
            return 0;
        }
        else
        {
            return Convert.ToChar(temp);
        }
    }

    public override void Write(string text)
    {
        this._bytestowrite += text.Length;
        int bytes_written = socket.Send(Encoding.ASCII.GetBytes(text));
        this._bytestowrite -= bytes_written;

        if (bytes_written != text.Length)
        {
            Console.WriteLine("socket.Send mismatched byte count, wrote " + bytes_written + " expected " + text.Length);
        }
    }

    public override void Write(char[] buffer, int offset, int count)
    {
        this._bytestowrite += count;
        int bytes_written = socket.Send(Encoding.ASCII.GetBytes(buffer, offset, count));
        this._bytestowrite -= bytes_written;

        if (bytes_written != count)
        {
            Console.WriteLine("socket.Send mismatched byte count, wrote " + bytes_written + " expected " + count);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        this._bytestowrite += count;
        int bytes_written = socket.Send(buffer, offset, count, SocketFlags.None);
        this._bytestowrite -= bytes_written;

        if(bytes_written != count)
        {
            Console.WriteLine("socket.Send mismatched byte count, wrote " + bytes_written + " expected " + count);
        }

    }

    public TCPTarget(string remoteHost, UInt32 remotePort) : base(remoteHost, remotePort)
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
            if(numBytesRead >= socketBufferSize)
            {
                Console.WriteLine("More bytes received than buffer can hold: " + numBytesRead);
            }

            if (numBytesRead > 0)
            {
                for (int i = 0; i < numBytesRead; i++)
                {
                    if (this.ringBuffer_Receive.Write(Convert.ToByte(socketBuffer_Receive[i])) < 0)
                    {
                        Console.WriteLine("buffer.Write() < 0");
                        return;
                    }
                }
                recvSocket.BeginReceive(socketBuffer_Receive, 0, socketBufferSize, 0, new AsyncCallback(RecieveCallback), recvSocket);
            }
            else
            {
                Console.WriteLine("Read 0 bytes...");
            }
        }
        catch (ObjectDisposedException)
        {

        }
        catch (Exception e)
        {
            Console.WriteLine("Source : " + e.Source);
            Console.WriteLine("Message : " + e.Message);
        }
    }
}
