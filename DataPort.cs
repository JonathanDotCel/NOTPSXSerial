using System;
using System.IO.Ports;

abstract public class DataPort
{
	public DataPort(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits) { }
	public DataPort(string remoteHost, UInt32 remotePort) { }

	abstract public int BytesToRead { get; }
	abstract public int BytesToWrite { get; }
	abstract public Handshake Handshake { get; set; }
	abstract public bool DtrEnable { get; set; }
	abstract public bool RtsEnable { get; set; }
	abstract public int ReadTimeout { get; set; }
	abstract public int WriteTimeout { get; set; }

	abstract public void Open();
	abstract public void Close();
	abstract public int ReadByte();
	abstract public int ReadChar();
	abstract public void Write(string text);
	abstract public void Write(char[] buffer, int offset, int count);
	abstract public void Write(byte[] buffer, int offset, int count);
}