using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

[Flags]
public enum LogType : byte {
    Debug = (1 << 0),
    Info = (1 << 1),
    Warning = (1 << 2),
    Error = (1 << 3),
    // Set the Log type to Stream to avoid overriding any colours from
    // the incoming stream.
    Stream = (1 << 4),
};

public class Log {


    // All events enabled by default
    private static LogType SubscribedEvents = LogType.Debug | LogType.Info | LogType.Warning | LogType.Error | LogType.Stream;

    // buffered output
    private static ConsoleColor originalColor = Console.ForegroundColor; // not sure whether that works here
    private static byte[] byteCache = new byte[ 256 ];
    private static int byteCount = 0;
    private static int currentlyFlushing = 0;
    private static Timer flushTimer;

    public static void SetLevel(LogType logTypes) {
        SubscribedEvents = logTypes | LogType.Stream;
    }

    private static void SetColorByLogType( LogType inType ) {

        if ( inType == LogType.Stream ){
            return;
        }

        ConsoleColor logColor;

        switch ( inType ) {
            case LogType.Debug: logColor = ConsoleColor.DarkGreen; break;
            case LogType.Info: logColor = ConsoleColor.White; break;
            case LogType.Warning: logColor = ConsoleColor.DarkYellow; break;
            case LogType.Error: logColor = ConsoleColor.DarkRed; break;
            default: return;
        }
        Console.ForegroundColor = logColor;
    }

    //public static void Write( string inMessage = "", LogType inType = LogType.Info ) {

    //    ConsoleColor originalColor = Console.ForegroundColor;

    //    if ( (SubscribedEvents & inType) == 0 )
    //        return;

    //    SetColorByLogType( inType );

    //    Console.Write( inMessage );

    //    if ( (inType & LogType.Stream) == 0 ){
    //        Console.ForegroundColor = originalColor;
    //    }
    //}

    // buffered output version, force flushed on timeout (only Write for now)
    public static void Write( string inMessage = "", LogType inType = LogType.Info ) {
        if ( (SubscribedEvents & inType) == 0 )
            return;

        SetColorByLogType( inType );

        byte[] messageBytes = Encoding.ASCII.GetBytes( inMessage ); // hmm
        int messageLength = messageBytes.Length;

        while ( currentlyFlushing == 1 ) {
            Thread.Sleep( 1 );
        }

        for ( int i = 0; i < messageLength; i++ ) {
            byteCache[ byteCount++ ] = messageBytes[ i ];

            if ( byteCount == 256 ) {
                Console.Write( Encoding.ASCII.GetString( byteCache ) );
                byteCount = 0;
            }
        }

        if ( (inType & LogType.Stream) == 0 ) {
            Console.ForegroundColor = originalColor;
        }

        ResetFlushTimer();
    }

    private static void FlushBuffer( object state ) {
        if ( byteCount > 0 ) {
            currentlyFlushing = 1;
            Console.Write( Encoding.ASCII.GetString( byteCache, 0, byteCount ) );
            currentlyFlushing = 0;
            byteCount = 0;
        }
    }

    private static void ResetFlushTimer() {
        flushTimer?.Dispose(); // Dispose previous timer instance if it exists

        // force flush every 15 millis, stays a bit under a PSX vsync interval
        flushTimer = new Timer( FlushBuffer, null, 15, Timeout.Infinite );
    }
    // END AI stuff

    public static void WriteLine( string inMessage = "", LogType inType = LogType.Info ) {

        ConsoleColor originalColor = Console.ForegroundColor;

        if ( (SubscribedEvents & inType) == 0 )
            return;

        SetColorByLogType(inType);

        Console.WriteLine( inMessage );

        if ( (inType & LogType.Stream) == 0 ) {
            Console.ForegroundColor = originalColor;
        }

    }

    public static void TestMessage() {
        WriteLine( "Debug", LogType.Debug );
        WriteLine( "Info", LogType.Info );
        WriteLine( "Warning", LogType.Warning );
        WriteLine( "Error", LogType.Error );
    }

}
