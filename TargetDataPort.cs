//
// Base class for e.g. Serial and TCP targets
//
// "Target" to distinguish between e.g. a remote TCP target and a local listen server for TCP<->UART bridge
//

using System;
using System.IO.Ports;

abstract public class TargetDataPort
{
    public TargetDataPort(string portName, SIOSPEED connectionType, int baudRate, Parity parity, int dataBits, StopBits stopBits) { }
	public TargetDataPort(string remoteHost, UInt32 remotePort, SIOSPEED connectionType ) { }

	abstract public int BytesToRead { get; }
	abstract public int BytesToWrite { get; }
	abstract public Handshake Handshake { get; set; }
	abstract public bool DtrEnable { get; set; }
	abstract public bool RtsEnable { get; set; }
	abstract public int ReadTimeout { get; set; }
	abstract public int WriteTimeout { get; set; }

    abstract public bool SkipAcks{ get; }

	abstract public void Open();
	abstract public void Close();
	abstract public int ReadByte();
	abstract public int ReadChar();
	abstract public void Write(string text);
	abstract public void Write(char[] buffer, int offset, int count);
	abstract public void Write(byte[] buffer, int offset, int count);
}