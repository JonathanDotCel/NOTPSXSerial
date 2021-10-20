using System;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;


public class TCPBridge : DataPort
{
	public static Socket socket;

	// Unused place-holders, counter-parts to serial properties used in program
	override public int BytesToRead { get; }
	override public int BytesToWrite { get; }
	override public Handshake Handshake { get; set; }
	override public bool DtrEnable { get; set; }
	override public bool RtsEnable { get; set; }



	override public int ReadTimeout
	{
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
		throw new NotImplementedException();
		return 0;
	}

    public override int ReadChar()
    {
		throw new NotImplementedException();
		return 0;
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
}
