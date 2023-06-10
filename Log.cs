using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

    public static void Write( string inMessage = "", LogType inType = LogType.Info ) {

        ConsoleColor originalColor = Console.ForegroundColor;

        if ( (SubscribedEvents & inType) == 0 )
            return;

        SetColorByLogType( inType );

        Console.Write( inMessage );

        if ( (inType & LogType.Stream) == 0 ){
            Console.ForegroundColor = originalColor;
        }
    }

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
