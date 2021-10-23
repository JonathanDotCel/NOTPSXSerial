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
		this.size = _size;
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
}

public class TCPBridge : DataPort
{
	public static Socket socket;
	public const int socketBufferSize = 512;
	public static byte[] socketBuffer = new byte[socketBufferSize];
	public static StringBuilder sb = new StringBuilder();
	public static string socketString; // oh boy, this will be nuts on the GC


	public RingBuffer ring_buffer = new RingBuffer(4096);
	private volatile int _bytestoread = 0;

	// Unused place-holders, counter-parts to serial properties used in program
	override public int BytesToRead { get { return _bytestoread; } }
	override public int BytesToWrite { get; }
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
            try
            {
				Console.WriteLine("Connected to " + ip + ":" + remoteEndpoint.Port);
				Console.WriteLine("Starting async receive task");
				socket.BeginReceive(socketBuffer, 0, socketBufferSize, 0, new AsyncCallback(RecieveCallback), socket);
			}
			catch (Exception e)
			{
				Console.WriteLine("Source : " + e.Source);
				Console.WriteLine("Message : " + e.Message);
			}

		}
		else
        {
			throw new System.Exception("Failed to connect to " + ip + ":" + remoteEndpoint.Port);
		}
	}

	public override void Close()
	{
		socket.Close();
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

		temp = this.ring_buffer.Read();

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

		temp = this.ring_buffer.Read();

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
		socket.Send(Encoding.ASCII.GetBytes(text));
	}

	public override void Write(char[] buffer, int offset, int count)
	{
		socket.Send(Encoding.ASCII.GetBytes(buffer, offset, count));
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		socket.Send(buffer, offset, count, SocketFlags.None);
	}

	public TCPBridge(string remoteHost, UInt32 remotePort) : base(remoteHost, remotePort)
	{
		socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		ip = IPAddress.Parse(remoteHost);
		remoteEndpoint = new IPEndPoint(ip, (int)remotePort);
	}

	private void RecieveCallback(IAsyncResult ar)
	{
		Socket recvSocket = (Socket)ar.AsyncState;
		int numBytesRead = recvSocket.EndReceive(ar);

		if (numBytesRead > 0)
		{
			for (int i = 0; i < numBytesRead; i++)
			{
				if (this.ring_buffer.Write(Convert.ToByte(socketBuffer[i])) < 0)
				{
					Console.WriteLine("buffer.Write() < 0");
					return;
				}
				else
				{
					this._bytestoread++;
				}
			}
			recvSocket.BeginReceive(socketBuffer, 0, socketBufferSize, 0, new AsyncCallback(RecieveCallback), recvSocket);
		}
		else
		{
			Console.WriteLine("Read 0 bytes...");
		}
	}
}
