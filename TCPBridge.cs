using System;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;


public class TCPBridge
{
	public static Socket socket;

	// Unused lace-holders, counter-parts to serial properties used in program

	static IPEndPoint remoteEndpoint;
	static IPAddress ip;

	public void Open()
	{
		try
		{
			Console.WriteLine("Opening remote connection to " + ip + ":" + remoteEndpoint.Port);
			socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
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
	}

	public void Close()
	{
		socket.Close();
	}

	public int ReadByte()
	{
		return 0;
	}

	public void Write(string text)
	{
		socket.Send(Encoding.ASCII.GetBytes(text));
	}

	public void Write(char[] buffer, int offset, int count)
	{
		socket.Send(Encoding.ASCII.GetBytes(buffer, offset, count));
	}

	public static void Init(string host, UInt32 remotePort)
	{
		InitClient(host, remotePort);
		ip = IPAddress.Parse(host);
		remoteEndpoint = new IPEndPoint(ip, (int)remotePort);
	}

	private static void InitClient(string host, UInt32 remotePort)
	{
		
	}
}
