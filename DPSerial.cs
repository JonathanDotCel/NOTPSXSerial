using System.IO.Ports;

public class DPSerial : DataPort
{
	private static SerialPort properSerial;

	public DPSerial(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits) : base(portName, baudRate, parity, dataBits, stopBits)
	{
		properSerial = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
	}

	public override int BytesToRead
	{
		get { return properSerial.BytesToRead; }
	}

	public override int BytesToWrite
	{
		get { return properSerial.BytesToWrite; }
	}

	public override Handshake Handshake
	{
		get { return properSerial.Handshake; }
		set { properSerial.Handshake = value; }
	}

	public override bool DtrEnable
	{
		get { return properSerial.DtrEnable; }
		set { properSerial.DtrEnable = value; }
	}
	public override bool RtsEnable
	{
		get { return properSerial.RtsEnable; }
		set { properSerial.RtsEnable = value; }
	}

	public override int ReadTimeout
	{
		get { return properSerial.ReadTimeout; }
		set { properSerial.ReadTimeout = value; }
	}
	public override int WriteTimeout
	{
		get { return properSerial.WriteTimeout; }
		set { properSerial.WriteTimeout = value; }
	}

	public override void Open()
	{ properSerial.Open(); }

	public override void Close()
	{ properSerial.Close(); }

	public override int ReadByte()
	{ return properSerial.ReadByte(); }

	public override int ReadChar()
	{ return properSerial.ReadChar(); }

	public override void Write(string text)
	{ properSerial.Write(text); }

	public override void Write(char[] buffer, int offset, int count)
	{ properSerial.Write(buffer, offset, count); }

	public override void Write(byte[] buffer, int offset, int count)
	{ properSerial.Write(buffer, offset, count); }
}