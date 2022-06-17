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

public enum MonitorMode { 
    // serial is monitored for printf, pcdrv, halt events, etc
    MONITOR_OR_PCDRV, 
    // as MONITOR_OR_PCDRV, but also forwarded to a socket
    SERIALBRIDGE,
    // as MONITOR_OR_PCDRV, but for GDB
    GDB 
};

/// <summary>
/// Monitor data over the target connection and optionally
/// listens on a socket in bridge mode or gdb mode
/// </summary>
public class Bridge {

    // TODO: could do to document some of these a bit better.

    public static bool enabled = false;

    public static TargetDataPort serial => Program.activeSerial;

    public static Socket socket;

    // default monitor mode, no external socket/gdb
    public static MonitorMode activeBridgeMode = MonitorMode.MONITOR_OR_PCDRV;

    public const int socketBufferSize = 512;
    public static byte[] socketBuffer = new byte[ socketBufferSize ];
    public static StringBuilder sb = new StringBuilder();
    public static string socketString; // oh boy, this will be nuts on the GC

    public static Socket replySocket;

    public static void Init( MonitorMode inMode, UInt32 localPort, string localIP = "" ) {

        activeBridgeMode = inMode;
        InitListenServer( localPort, localIP );

        if ( inMode == MonitorMode.GDB ) {
            GDBServer.Init();
            Console.WriteLine( $"Monitoring psx and accepting GDB connections on {localIP}:{localPort}" );
        }

        // Shared function
        MonitorSerial();

    }

    // Called when a connection has been accepted
    public static void AcceptCallback( IAsyncResult result ) {

        Socket whichSocket = (Socket)result.AsyncState;
        Socket newSocket = socket.EndAccept( result );

        Console.WriteLine( "Remote connection to local socket accepted: " + newSocket.LocalEndPoint );

        replySocket = newSocket;

        // on the new socket or it'll moan. I don't like this setup.
        newSocket.BeginReceive( socketBuffer, 0, socketBufferSize, 0, new AsyncCallback( ReceiveCallback ), newSocket );

    }

    /// <summary>
    /// Received data over a socket:
    /// - in bridge mode
    /// - from GDB
    /// </summary>
    /// <param name="ar">A <see cref="Socket" var./></param>
    private static void ReceiveCallback( IAsyncResult ar ) {

        //Console.WriteLine( "SOCKET: RCB " + ar.AsyncState );

        Socket recvSocket = (Socket)ar.AsyncState;

        //Console.WriteLine( "SOCKET RCB 1 " + recvSocket );

        SocketError errorCode;
        int numBytesRead = recvSocket.EndReceive( ar, out errorCode );

        if( errorCode != SocketError.Success ) {
            if(errorCode == SocketError.ConnectionReset ) {
                Console.WriteLine( "Remote connection closed, restarting listen server" );
                Console.WriteLine( "CTRL-C to exit" );
                recvSocket.Close();
                recvSocket.BeginReceive( socketBuffer, 0, socketBufferSize, 0, new AsyncCallback( ReceiveCallback ), recvSocket );
            }
            Console.WriteLine( "errorCode: " + errorCode.ToString() );
            return;
        }

        //Console.WriteLine( "SOCKET RCB 2 " + numBytesRead );

        if ( numBytesRead > 0 ) {

            // copy the bytes (from the buffer specificed in BeginRecieve), into the stringbuilder
            string thisPacket = ASCIIEncoding.ASCII.GetString( socketBuffer, 0, numBytesRead );
            sb.Append( thisPacket );

            socketString = sb.ToString();
            //Console.WriteLine( "\rSOCKET: rec: " + thisPacket );

            if ( socketString.IndexOf( "<EOF>" ) > -1 ) {

                Console.WriteLine( "Read {0} bytes, done!: {1}", numBytesRead, socketString );

            } else {


                if ( activeBridgeMode == MonitorMode.GDB ) {

                    GDBServer.ProcessData( socketString );
                    sb.Clear();

                } else
                if ( activeBridgeMode == MonitorMode.SERIALBRIDGE ) {

                    // To echo it back:
                    //Send( recvSocket, thisPacket );

                    // Send the incoming socket data over sio
                    TransferLogic.activeSerial.Write( socketBuffer, 0, numBytesRead );

                }

                //Console.WriteLine( "SOCKET: Grabbing more data" );

                try {
                    recvSocket.BeginReceive( socketBuffer, 0, socketBufferSize, 0, new AsyncCallback( ReceiveCallback ), recvSocket );
                } catch ( Exception ex ) {
                    Console.WriteLine( "SOCKET: RCB EXCEPTION: " + ex.Message );
                }

            }


        } else {
            Console.WriteLine( "Read 0 bytes..." );
        }

    }

    public static void Send( string inData ) {
        Send( replySocket, inData );
    }


    public static void Send( Socket inSocket, string inData ) {

        // TODO: we could probs check if there's
        //       any sort of connection long before it
        //       reaches this point.
        if ( inSocket == null ) {
            return;
        }

        byte[] bytes = ASCIIEncoding.ASCII.GetBytes( inData );

        inSocket.BeginSend( bytes, 0, bytes.Length, 0, new AsyncCallback( SendCallback ), inSocket );

    }

    // Send() succeeded
    private static void SendCallback( IAsyncResult ar ) {

        Socket whichSocket = (Socket)ar.AsyncState;

        int bytesSent = whichSocket.EndSend( ar );
        Console.WriteLine( "Sent {0} bytes ", bytesSent );

        //socket.Shutdown( SocketShutdown.Both );
        //socket.Close();

    }

    //
    // Listens on the given IP and port
    // Shared by bridge and gdb modes
    //
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



    /// <summary>
    /// Monitor serial data from the psx
    /// 
    /// 1: printfs() over serial
    /// 2: PCDRV
    /// 3: `HALT` (break/exception/etc)
    /// 
    /// + In bridge mode:
    /// 4: forwards serial traffic over a TCP socket (because mono hasn't implemented SIO callbacks)
    ///
    /// + In GDB mode:
    /// 5: forwards HALT etc to GDB
    /// 
    /// </summary>

    // TODO: move somewhere appropriate
    public static void MonitorSerial() {

        const int ESCAPECHAR = 0x00;
        // Old mode (unescaped): when this reads HLTD, kdebug has halted the playstation.
        char[] last4ResponseChars = new char[] { 'x', 'x', 'x', 'x' };

        // 10kb buffer?
        byte[] responseBytes = new byte[ 2048 * 10 ];
        int bytesInBuffer = 0;

        bool lastByteWasEscaped = false;

        while ( true ) {

            // Ensure that socket threads aren't trying
            // to e.g. read/write memory at the same time
            lock( SerialTarget.serialLock ) {

                while ( serial.BytesToRead > 0 && bytesInBuffer < responseBytes.Length ) {

                    int thisByte = (byte)serial.ReadByte();
                    bool thisByteIsEscapeChar = (thisByte == ESCAPECHAR);

                    //Console.WriteLine( $"Got val {thisByte.ToString( "X" )} escaped={thisByteIsEscapeChar} lastWasEscapeChar={lastByteWasEscaped}" );

                    // The byte before this one was an escape sequence...
                    if ( lastByteWasEscaped ) {

                        // 2x escape cars = just print that char
                        if ( thisByteIsEscapeChar ) {

                            // a properly escaped doublet can go in the buffer.
                            responseBytes[ bytesInBuffer++ ] = ESCAPECHAR;
                            Console.Write( (char)thisByte );

                        } else {

                            if ( thisByte == 'p' ) {

                                PCDrv.ReadCommand();

                            }

                        }

                        // whether we're printing an escaped char or acting on
                        // a sequence, reset things back to normal.
                        lastByteWasEscaped = false;
                        continue; // next inner loop

                    }

                    // Any non-escape char: print it, dump it, send it, etc
                    if ( !thisByteIsEscapeChar ) {

                        responseBytes[ bytesInBuffer++ ] = (byte)thisByte;
                        Console.Write( (char)thisByte );

                        // TODO: remove this unescaped method after a few versions
                        // Clunky way to do it, but there's no unboxing or reallocation
                        last4ResponseChars[ 0 ] = last4ResponseChars[ 1 ];
                        last4ResponseChars[ 1 ] = last4ResponseChars[ 2 ];
                        last4ResponseChars[ 2 ] = last4ResponseChars[ 3 ];
                        last4ResponseChars[ 3 ] = (char)thisByte;
                        if (
                            last4ResponseChars[ 0 ] == 'H' && last4ResponseChars[ 1 ] == 'L'
                            && last4ResponseChars[ 2 ] == 'T' && last4ResponseChars[ 3 ] == 'D'
                        ) {
                            Console.WriteLine( "PSX may have halted (<8.0.I)!" );
                        
                            GDBServer.GetRegs();
                            GDBServer.DumpRegs();
                            if ( GDBServer.isStepBreakSet ) {
                                TransferLogic.Unhook();
                                GDBServer.isStepBreakSet = false;
                            }
                            GDBServer.SetHaltStateInternal( GDBServer.HaltState.HALT, true );
                        }

                    }

                    lastByteWasEscaped = thisByteIsEscapeChar;

                } // bytestoread > 0

                if ( !GDBServer.enabled ) {
                    SocketError errorCode;
                    // Send the buffer back in a big chunk if we're not waiting
                    // on an escaped byte resolving			
                    if ( bytesInBuffer > 0 && !lastByteWasEscaped ) {
                        // send it baaahk
                        if ( replySocket != null ) {
                            replySocket.Send( responseBytes, 0, bytesInBuffer, SocketFlags.None, out errorCode );
                        }
                        bytesInBuffer = 0;
                    }

                    if ( Console.KeyAvailable ) {
                        ConsoleKeyInfo keyVal = Console.ReadKey( true );
                        serial.Write( new byte[] { (byte)keyVal.KeyChar }, 0, 1 );
                        Console.Write( keyVal.KeyChar );
                    }
                }

            } // serial lock object

            // Yield the thread
            Thread.Sleep( 1 );

        } // while

    }


}
