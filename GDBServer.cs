// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

//
// GDB TCP->SIO bridge for Unirom 8
//
// NOTE!
//
// While all of the basic debug functionality is present, the GDB
// bridge is still very much a work in progress!
//

using System;
using System.Text;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading;

public class GDBServer {

    public static bool enabled = false;

    public static TargetDataPort serial => Program.activeSerial;

    public static Socket socket;

    /*struct GDBPacket {
        public byte[] data;
        public int len;
        public int pos;
    }*/

    // qSupported
    struct GDBClientFeatures {
        bool multiprocess;
        bool xmlRegisters;
        bool qRelocInsn;
        bool swbreak;
        bool hwbreak;
        bool fork_events;
        bool vfork_events;
        bool exec_events;
        bool vContSupported;
    }


    public static void DebugInit( UInt32 localPort, string localIP = "" ) {

        InitListenServer( localPort, localIP );
        //MonitorSerialToSocket();

    }

    public const int socketBufferSize = 512;
    public static byte[] socketBuffer = new byte[ socketBufferSize ];
    public static StringBuilder sb = new StringBuilder();
    public static string socketString; // oh boy, this will be nuts on the GC

    public static Socket replySocket;

    public static void AcceptCallback( IAsyncResult result ) {

        Socket whichSocket = (Socket)result.AsyncState;
        Socket newSocket = socket.EndAccept( result );

        //Console.WriteLine( "Remote connection to local socket accepted: " + whichSocket.LocalEndPoint );
        Console.WriteLine( "Remote connection to local socket accepted: " + newSocket.LocalEndPoint );

        replySocket = newSocket;

        // on the new socket or it'll moan. I don't like this setup.
        newSocket.BeginReceive( socketBuffer, 0, socketBufferSize, 0, new AsyncCallback( RecieveCallback ), newSocket );
    }

    private static void RecieveCallback( IAsyncResult ar ) {

        Socket recvSocket = (Socket)ar.AsyncState;

        SocketError errorCode;
        int numBytesRead = recvSocket.EndReceive( ar, out errorCode );

        if ( errorCode != SocketError.Success ) {
            if ( errorCode == SocketError.ConnectionReset ) {
                Console.WriteLine( "Remote connection closed, restarting listen server" );
                Console.WriteLine( "CTRL-C to exit" );
                recvSocket.Close();
                recvSocket.BeginReceive( socketBuffer, 0, socketBufferSize, 0, new AsyncCallback( RecieveCallback ), recvSocket );
            }
            Console.WriteLine( "errorCode: " + errorCode.ToString() );
            return;
        }

        if ( numBytesRead > 0 ) {

            // copy the bytes (from the buffer specificed in BeginRecieve), into the stringbuilder
            string thisPacket = ASCIIEncoding.ASCII.GetString( socketBuffer, 0, numBytesRead );
            sb.Append( thisPacket );

            socketString = sb.ToString();

            if ( socketString.IndexOf( "<EOF>" ) > -1 ) {

                Console.WriteLine( "Read {0} bytes, done!: {1}", numBytesRead, socketString );

            } else {
                ProcessData( socketString );
                sb.Clear();
                recvSocket.BeginReceive( socketBuffer, 0, socketBufferSize, 0, new AsyncCallback( RecieveCallback ), recvSocket );

            }


        } else {
            Console.WriteLine( "Read 0 bytes..." );
        }

    }

    private static string CalculateChecksum( string packet ) {

        int checksum = 0;
        foreach ( char c in packet ) {
            checksum += (int)c;
        }
        checksum %= 256;
        return checksum.ToString( "x" );
    }

    private static void ProcessData( string Data ) {
        char[] packet = Data.ToCharArray();
        string packetData = "";
        string our_checksum = "0";
        int offset = 0;
        int size = Data.Length;

        Console.WriteLine( "Processing data: " + Data );
        while ( size > 0 ) {
            char c = packet[ offset++ ];
            size--;
            if ( c == '+' ) {
                Console.WriteLine( "ACK" );
                SendAck();
            }
            if ( c == '$' ) {
                Console.WriteLine( "Got a packet" );
                int end = Data.IndexOf( '#', offset );
                if ( end == -1 ) {
                    Console.WriteLine( "No end of packet found" );
                    return;
                }

                packetData = Data.Substring( offset, end - offset );
                Console.WriteLine( "Packet data: " + packetData );
                our_checksum = CalculateChecksum( packetData );
                size -= (end - offset);
                offset = end;
                Console.WriteLine( "Size remaining: " + size );
                Console.WriteLine( "Data remaining: " + Data.Substring( offset ) );
            } else if ( c == '#' ) {
                string checksum = Data.Substring( offset, 2 );
                Console.WriteLine( "Checksum: " + checksum );
                Console.WriteLine( "Our checksum: " + our_checksum );
                if ( checksum.Equals( our_checksum ) ) {
                    Console.WriteLine( "Checksums match!" );
                    SendAck();
                    Send( replySocket, "$" + packetData + "#" + CalculateChecksum( packetData ));
                    //ProcessPacket( packetData );
                } else {
                    Console.WriteLine( "Checksums don't match!" );
                }
                offset += 2;
                size -= 3;
            }
            else if ( c == '-' ) {
                Console.WriteLine( "NACK" );
                SendNAck();
            }
        }
    }

    private static void Send( Socket inSocket, string inData ) {

        byte[] bytes = ASCIIEncoding.ASCII.GetBytes( inData );

        inSocket.BeginSend( bytes, 0, bytes.Length, 0, new AsyncCallback( SendCallback ), inSocket );

    }

    private static void SendAck() {
        Send( replySocket, "+" );
        Console.WriteLine( "+" );
    }

    private static void SendNAck() {
        Send( replySocket, "-" );
        Console.WriteLine( "-" );
    }

    private static void SendCallback( IAsyncResult ar ) {

        Socket whichSocket = (Socket)ar.AsyncState;

        int bytesSent = whichSocket.EndSend( ar );
        //Console.WriteLine( "Sent {0} bytes ", bytesSent );

        //socket.Shutdown( SocketShutdown.Both );
        //socket.Close();

    }

    private static void InitListenServer( UInt32 inPort, string localIP = "" ) {

        IPAddress ip;

        if ( string.IsNullOrEmpty( localIP ) ) {
            ip = IPAddress.Parse( "127.0.0.1" );
        } else {
            Console.WriteLine( "Binding IP " + localIP );
            ip = IPAddress.Parse( localIP );
        }

        Console.WriteLine( "Opening a listen server on " + ip + ":" + inPort );

        IPEndPoint localEndpoint = new IPEndPoint( ip, (int)inPort );

        socket = new Socket( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );

        socket.Bind( localEndpoint );

        socket.Listen( 2 );

        socket.BeginAccept( new AsyncCallback( AcceptCallback ), socket );

    }
}
