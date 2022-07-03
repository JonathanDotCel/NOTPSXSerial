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
    Error = (1 << 3)
};

public class Log {


    // All events enabled by default
    private static LogType SubscribedEvents = LogType.Debug | LogType.Info | LogType.Warning | LogType.Error;

    public static void SetLevel(LogType LogTypes) {
        SubscribedEvents = LogTypes;
    }

    private static void SetColorByLogType( LogType LogType ) {
        ConsoleColor logColor;

        switch ( LogType ) {
            case LogType.Debug: logColor = ConsoleColor.DarkGreen; break;
            case LogType.Info: logColor = ConsoleColor.White; break;
            case LogType.Warning: logColor = ConsoleColor.DarkYellow; break;
            case LogType.Error: logColor = ConsoleColor.DarkRed; break;
            default: return;
        }
        Console.ForegroundColor = logColor;
    }

    public static void Write( string Message = "", LogType LogType = LogType.Info ) {
        ConsoleColor original_color = Console.ForegroundColor;

        if ( (SubscribedEvents & LogType) == 0 )
            return;

        SetColorByLogType( LogType );

        Console.Write( Message );

        Console.ForegroundColor = original_color;
    }

    public static void WriteLine( string Message = "", LogType LogType = LogType.Info ) {
        ConsoleColor original_color = Console.ForegroundColor;

        if ( (SubscribedEvents & LogType) == 0 )
            return;

        SetColorByLogType(LogType);

        Console.WriteLine( Message );

        Console.ForegroundColor = original_color;
    }

    public static void TestMessage() {
        WriteLine( "Debug", LogType.Debug );
        WriteLine( "Info", LogType.Info );
        WriteLine( "Warning", LogType.Warning );
        WriteLine( "Error", LogType.Error );
    }

}
