//
//
// NoPS - NotPsxSerial
// Feb 2020 - github.com/JonathanDotCel
//
// 
// GPL BEER LICENSE.
// It's the same as the GPL3, but if you fork it, you must send me a beer and leave the original headers.
// As an addition, only add credits; never remove.
//
// 
// This is the PC-side companion to Unirom8 with backwards compat for Unirom7/PSXSerial format.
// 
// 03_05_2020 - Release 2
//    - Added the continous hex dump mode
//    - Removed deprecated response enums
//
// 10_05_2020 - Release 3
//    - Added the /fast option for 512k baud
//    - Ability to use smaller chunks for CP210x UARTs, etc
//    - Hidden /verify switch for checking faster connections
//    - Linux support thanks to r0r0
//	
// 26_05_2020 - Release 4
//    - Now supports binary upload/download during gameplay through Unirom 8.0.B4
//	    /debug to enable this via Unirom's debug mode
//    - /reset to reset the machine
//    - /fast improvements on the other end
//    - "unsp" response for unsupported command (unirom in debug mode)
//    - Remembers your last-used com port
//

// 18_06_2020 - Release 5
//    - .exe file size is now calculated as ( total - 0x800 )
//    - 2x stop bits by default for all modes now
//    - fast sio will now use odd parity
//

// 15_08_2020 - Release 9
//    - restructuring for GDB implementation
//    - removed the /verify command
//    - ping/pong
//    - faster startup
//    - halt / cont commands
//    - warnings for no dbg or dbg only
// 


#define DebugArgs

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Linq;



public enum CommandMode {

	NOT_SET,

	[Command( "/bin" )]
	[NeedsInputFile]
	[NeedsAddress]
	[Challenge("SBIN")]
	[Response("OKAY")]
	SEND_BIN,

	[Command( "/rom" )]
	[NeedsInputFile]
	[Challenge("SROM")]
	[Response("OKAY")]
	SEND_ROM,

	[Command( "/exe" )]
	[NeedsInputFile]
	[Challenge("SEXE")]
	[Response("OKAY")]
	SEND_EXE,


	[Command( "/jmp" )]
	[Challenge("JUMP")]
	[Response("OKAY")]
	[NeedsAddress]
	JUMP_JMP,
		
	[Command( "/jal" )]
	[Challenge("CALL")]
	[Response("OKAY")]
	[NeedsAddress]
	JUMP_CALL,

	[Command( "/dump" )]
	[Challenge("DUMP")]
	[Response("OKAY")]
	[NeedsAddress]
	[NeedsSize]
	DUMP,

	[Command( "/poke" )]
	[Challenge("SBIN")]
	[Response("OKAY")]
	[NeedsAddress]
	[NeedsSize]				// abused as a 2nd hexa param
	POKE,

	[Command( "/watch" )]
	[Challenge("HEXD")]
	[Response("OKAY")]
	[NeedsAddress]
	[NeedsSize]
	WATCH,
	
	[Command( "/reset" )]
	[Challenge("REST")]
	[Response("OKAY")]	
	RESET,

	[Command( "/ping" )]
	[Challenge("PING")]
	[Response("PONG")]	
	PING,

	// Debug mode functions

	// install Unirom kernel-resident debug SIO
	[Command( "/debug" )]
	[Challenge("DEBG")]
	[Response("OKAY")]		
	DEBUG,

	// also sends DEBG (above) to install kernel-resident SIO
	[Command( "/gdb" )]
	[Challenge("DEBG")]
	[Response("OKAY")]
	GDB,

	[Command( "/halt" )]
	[Challenge("HALT")]
	[Response("HLTD")]
	HALT,

	[Command( "/cont" )]
	[Challenge("CONT")]
	[Response("OKAY")]
	CONT,

	[Command( "/regs" )]	
	REGS,

	[Command( "/setreg" )]	
	[NeedsRegister]
	[NeedsAddress]		// abused as a 32bit hexa value
	SETREG,

	/*
	// Deprecated as of 8.0.C's self-correcting algo
	[Command( "/verify" )]
	[Challenge("SBIN")]
	[Response("OKAY")]
	[NeedsInputFile]
	[NeedsAddress]	
	VERIFY,
	*/
		

	COUNT

};


internal partial class Program {

	const string VERSION = "v9.Matthew";
	const int TIMEOUT = 500;

	static void PrintUsage( bool justTheTip = false ) {

		#if !DebugArgs
		if( !justTheTip ){
			Console.Clear();
		}
		#endif

		Console.ForegroundColor = ConsoleColor.White;

		// assuming 80 columns

		Console.Write( "\n" );
		Console.Write( "================================================================================\n" );
		Console.Write( "    Totally NOtPsxSerial " + VERSION + "\n" );
		Console.Write( "    Thanks: Jihad of HITMEN, Shendo, Type79, Dax & r0r0\n" );
		Console.Write( "    Not shadow though...\n" );
		Console.Write( "================================================================================\n" );
		Console.Write( "\n" );

		// On a serious note though before the rewrite the first few versions were straight decompiles
		// of PSXSerial with minor tweaks for comic effect. Cheers to Matt/Shadow for being super chill about that.

		Console.Write( "	Note: You may have to install mono and launch via 'mono nops.exe /args if...\n" );
		Console.Write( "      - Windows cant put your serial device (FTDI, etc) into 115200baud\n" );
		Console.Write("       - You are using a Sharklink/Net Yaroze cable\n");
		Console.Write("       - Any kinda linux\n");
		Console.Write( "\n\n" );

		if( justTheTip ) return;

		if ( POSIXEnvironment )
			suggestedComPort = "/dev/ttyUSB0";

		Console.Write( "    Usage: NOPS.EXE [/args] FILENAME COMPORT" );
		Console.Write( "\n\n" );

		Console.Write( "      Send an .EXE :  NOPS.EXE /exe <FILE.EXE> {0}\n\n", suggestedComPort );

		Console.Write( "      Flash a .ROM :  NOPS.EXE /rom <FILE.ROM> {0}\n\n", suggestedComPort );

		Console.Write( "      Send a  .BIN :  NOPS.EXE /bin 0xADDRESS0 <FILE.BIN> {0}\n\n", suggestedComPort );	
		
		Console.Write( "      Jump addr (jr) :   NOPS.EXE /jmp 0xADDRESS0 {0}\n", suggestedComPort );
		Console.Write( "      Call addr (jal):   NOPS.EXE /jal 0xADDRESS0 {0}\n\n", suggestedComPort );
		
		Console.Write( "    Dumpy Stuff:\n" );
		Console.Write( "		RAM  (  2m) :   NOPS.EXE /dump 0x80000000 0x200000 {0}\n", suggestedComPort );
		Console.Write( "		ROM  (128k) :   NOPS.EXE /dump 0x1F000000 0x20000  {0}\n", suggestedComPort );
		Console.Write( "		ROM  (384k) :   NOPS.EXE /dump 0x1F000000 0x60000  {0}\n", suggestedComPort );
		Console.Write( "		ROM  (512k) :   NOPS.EXE /dump 0x1F000000 0x80000  {0}\n", suggestedComPort );
		Console.Write( "		BIOS (512k) :   NOPS.EXE /dump 0xBFC00000 0x80000  {0}\n", suggestedComPort );
		Console.Write( "		SPAD (  1k) :   NOPS.EXE /dump 0x1F800000 0x400    {0}\n\n", suggestedComPort );

		Console.Write( "    Continuous Hex Dump (to screen):\n" );
		Console.Write( "		CD REGS:   NOPS.EXE /watch 0x1F801800 0x4 {0}\n\n", suggestedComPort );

		Console.Write( "    Extra switches:\n" );		
		Console.Write( "      /m to open the Serial IO monitor (can be used /m {0} alone)\n", suggestedComPort );
		Console.Write( "      /fast to enable or continue using 500k baud\n" );
		Console.Write( "      /debug to enable experimental /bin & /dump during gameplay\n" );
		Console.Write( "\n" );

	}

	public static void Blip(){
		Console.WriteLine( "Blip" );
	}

	// public stuff
	public static SerialPort activeSerial;
	public static UInt32 protocolVersion = 1;

	// less public stuff

	static string argComPort = "";
	static string suggestedComPort = "COM8";
	
	static CommandMode argCommand = CommandMode.NOT_SET;
		

	static UInt32 argAddr;
	static UInt32 argSize;
	static bool satisfiedAddressRequirements = false;
	static bool satisfiedSizeRequirements = false;
	static bool satisfiedRegisterRequirements = false;

	static bool needInputFile = false;
	static bool needOutputFile = false;	
	static bool satisfiedFileRequirements = false;
	static string argFileName;
	static byte[] inFile;
	static bool fastMode = false;
	
	// technically not just limited to registers
	static string argRegister = "";

	// keep it globally scoped so we can process it later
	static byte[] lastReadBytes;

	static bool monitorComms = false;
	static bool usingCachedComPort = false;
	
	//static UInt32 checkSum = 0; // in or out
	

	// Ensure the input is vaguely sane.
	// 
	// - Make a list of args
	// - Remove them from the list when they've been processed
	// - Determine COM port
	// - Grab any /r /e /b commands
	// - Get the address value if required
	// - Get the other address value?
	// - File name!
	// 


	static bool VerifyArgs( string[] inArgs ) {
		
		if ( inArgs.Length == 0 ){
			PrintUsage( false );
			return false;
		}

		// Thank you, linq <3
		// We'll remove args as they're processed so they're not processed twice
		// and extra args will be left over.

		List<string> remainingArgs = inArgs.ToList();

		// Specified a com port?

		#if DebugArgs
		Console.WriteLine( "__DEBUG__Argsleft: " + remainingArgs.Count );
		#endif

		for( int i = remainingArgs.Count - 1; i >= 0; i-- ) {

			string s = remainingArgs[ i ];

			if ( ValidPort( s ) ){
				argComPort = s;
				remainingArgs.RemoveAt( i );
				break;
			}

		}

		// Likewise get the "/FAST" parameter out of the way

		for( int i = remainingArgs.Count - 1; i >= 0; i-- ) {

			string s = remainingArgs[ i ].ToUpperInvariant();

			if( s == "/FAST" ) {				
				fastMode = true;
				remainingArgs.RemoveAt( i );
			}

		}
		


		if( argComPort =="" ) {
			
			try{
				if ( System.IO.File.Exists( "comport.txt" ) ){
					argComPort = File.ReadAllText( "comport.txt" );
				}
			} catch ( System.Exception e ){
				Console.Write( "Error checking for cached com port...\n" + e );
			}

			if ( argComPort == "" ){
				if( POSIXEnvironment ) {
					return Error( "\nERROR! Please specify a device - e.g. /dev/ttyUSB0\n" );
				} else {
					return Error( "\nERROR! Please specify a COM port - e.g. COM8\n" );
				}
			} else {
				usingCachedComPort = true;
			}
			
		} else {
			
			

			try{
				File.WriteAllText( "comport.txt", argComPort );
			} catch ( System.Exception e ){
				Console.Write( "Error writing cached com port!\n" + e );
			}

		}


		// is /m the last remaining arg?
		if ( remainingArgs.Count == 1  && remainingArgs[0].ToLowerInvariant() == "/m" ){			
			monitorComms = true;
			return true;
		}


		#if DebugArgs
		Console.WriteLine( "__DEBUG__Argsleft: " + remainingArgs.Count );
		Console.WriteLine( "__DEBUG__COM: " + argComPort );
		Console.WriteLine( "__DEBUG__FAST: " + fastMode );
		#endif
		
		// Specified a command arg (or severay for future-proofing)?

		for( int arg = remainingArgs.Count - 1; arg >= 0; arg-- ) {

			string argVal = remainingArgs[ arg ].ToLowerInvariant();

			if ( argVal == "/m" ){
				
				#if DebugArgs
				Console.WriteLine( "__DEBUG__CommsMonitor Enabled: ");
				#endif

				monitorComms = true;
				remainingArgs.RemoveAt( arg );
				continue;
			}
			
			// first one is uninitialised... ignore it
			for( int j = 1; j < (int)CommandMode.COUNT; j++ ) {

				CommandMode c = (CommandMode)j;

				if( argVal.ToLowerInvariant() == c.command() ) {

					// Set the current value and remove
					// it from the list of available args
					argCommand = c;
					needInputFile = c.needsInputFile();
					needOutputFile = c.needsOutputFile();
					remainingArgs.RemoveAt( arg );

					// On top of that... do we need a size? E.g. for dumping bios.
					if ( argCommand.needsRegister() ){
						
						#if DebugArgs
						Console.WriteLine( "__DEBUG__Command " + c + " requires a register " );
						#endif

						// reassign it
						argVal = remainingArgs[ arg ].ToLowerInvariant();

						// TODO: verify it's a register, I guess.
						argRegister = argVal;

						remainingArgs.RemoveAt( arg );
						satisfiedRegisterRequirements = true;

					}

					// Now we've removed the /command, if there's
					// an address after it, it will be remaningArgs[ arg ]
					
					// found the command... do we need to find an address?
					if( argCommand.needsAddress() ) {
						
						// end of the array!
						if( arg >= remainingArgs.Count ) {
							return Error( "Specify an address in the format 0x01234567\n" );
						}

						#if DebugArgs
						Console.WriteLine( "__DEBUG__Command " + c + " requires an address " );
						#endif

						// reassign it						
						argVal = remainingArgs[ arg ].ToLowerInvariant();

						// try and get the address argument
						try{
							argAddr = ParseHexa( argCommand, argVal );
						} catch( System.Exception ){
							return Error( "EXCEPTION: Specify an address in the format 0x01234567" );
						}

						remainingArgs.RemoveAt( arg );
						satisfiedAddressRequirements = true;

					}

					// On top of that... do we need a size? E.g. for dumping bios.
					if ( argCommand.needsSize() ){
						
						#if DebugArgs
						Console.WriteLine( "__DEBUG__Command " + c + " requires a size parameter " );
						#endif

						// reassign it
						argVal = remainingArgs[ arg ].ToLowerInvariant();

						// try to get the size argument
						try{
							argSize = ParseHexa( argCommand, argVal );
						} catch ( System.Exception ){
							return Error( "EXCEPTION: Specify a size in the format 0x01234567" );
						}

						remainingArgs.RemoveAt( arg );
						satisfiedSizeRequirements = true;

					}



					break; // outer loop to check new args

				}

			} // inner loop


		} // outer loop

		#if DebugArgs
		Console.WriteLine( "__DEBUG__Argsleft: " + remainingArgs.Count );
		Console.WriteLine( "__DEBUG__COM: " + argComPort );
		Console.WriteLine( "__DEBUG__COMMAND: " + argCommand );
		Console.WriteLine( "__DEBUG__ADDR: " + argAddr.ToString("X8") );
		Console.WriteLine( "__DEBUG__NEEDSADDR: " + argCommand.needsAddress() );
		Console.WriteLine( "__DEBUG__NEEDSINPUT: " + argCommand.needsInputFile() );
		Console.WriteLine( "__DEBUG__NEEDSOUTPUT: " + argCommand.needsOutputFile() );
		#endif
				
		if( argCommand == CommandMode.NOT_SET ) {
			return Error( "Please specify a command - e.g. /r, /e /b, etc!\n\n" );
		}

		if ( !argCommand.needsRegister() )
			satisfiedRegisterRequirements = true;

		if ( !argCommand.needsAddress() )
			satisfiedAddressRequirements = true;

		if ( !argCommand.needsSize() )
			satisfiedSizeRequirements = true;

		if( !satisfiedAddressRequirements ) {
			return Error( "Did you specify an address or hex value? E.g. 0x23456788\n" );
		}

		if ( !satisfiedSizeRequirements ){
			return Error( "Did you specify a size? E.g. 0x23456788\n" );
		}

		if ( !satisfiedRegisterRequirements ){
			return Error( "Did you specify a register? E.g. a0" );
		}
		

		// We've parsed the arguments, the addresses and the COM port
		// Finish up by finding the file, if necessary

		if( !needInputFile && !needOutputFile ) {

			satisfiedFileRequirements = true;

		} else {

			// One of the args specifies a file?
			for( int i = remainingArgs.Count - 1; i >= 0; i-- ) {

				string fName = remainingArgs[ i ];

				if( needInputFile ) {
					
					argFileName = fName;

					try {
						inFile = File.ReadAllBytes( fName );
					} catch( System.Exception e ) {

						return Error( "Couldn't open input file " + fName + " Exception: " + e );

					}

					satisfiedFileRequirements = true;
					remainingArgs.RemoveAt( i );

				}
				
			}

		}
		
		if( !satisfiedFileRequirements ) {

			return Error( "Specify a filename!" );

		}

		#if DebugArgs
		Console.WriteLine( "__DEBUG__Argsleft: " + remainingArgs.Count );
		Console.WriteLine( "__DEBUG__FILENAME: " + argFileName );		
		Console.WriteLine( "__DEBUG__INPUTFILE: " + inFile );
		//Console.WriteLine( "__DEBUG__OUTPUTFILE: " + argOutFile );
		#endif

		// there shouldn't be any arguments left!
		if( remainingArgs.Count > 0 ){
			
			for( int i = remainingArgs.Count -1; i >= 0; i-- ){
				
				Error( "Unknown arg! " + remainingArgs[i] );

			}	

			return false;

		}

		// All done
		return true;

	} //VerifyArgs

	static UInt32 ParseHexa( CommandMode inCommand, string inString ) {

		string iLower = inString.ToLowerInvariant();
		iLower = iLower.Replace( inCommand.command().ToLowerInvariant(), "" );

		#if DebugArgs
		Console.WriteLine( "Parsing hexa " + inString );
		#endif

		// Whatever's left should be the address

		UInt32 outAddr = (uint)Convert.ToInt32( iLower, 16 );

		Console.Write( " - Using addr: 0x" + outAddr.ToString( "X8" ) + "\n" );

		return outAddr;

	}

	public static bool Error( string inString, bool printHeader = true ) {

		if ( printHeader )
			PrintUsage( false );
		
		Console.ForegroundColor = ConsoleColor.Red;
		Console.Write( "\n\n" );
		Console.Write( "ERROR! " + inString + " \n \n " );

		// Leaves the user with a green console.
		// Because we can. Shh, don't tell them till they get it right.
		Console.ForegroundColor = ConsoleColor.Green;

		return false;

	}

	// can skip the first sector on .exe files since it's 
	// just a couple of relevant 32bit values
	public static UInt32 CalculateChecksum( byte[] inBytes, bool skipFirstSector = false ){
		
		UInt32 returnVal = 0;
		for( int i = (skipFirstSector ? 2048 : 0); i < inBytes.Length; i++ ){
			returnVal += (UInt32)inBytes[i];
		}
		return returnVal;

	}


	private static void Main( string[] args ) {
		
		
		if ( !VerifyArgs( args ) ){
			return;
		}

		Transfer();

	}


	private static bool Transfer() {
		
		
		PrintUsage( true );


		activeSerial = new SerialPort( argComPort, 115200, Parity.None, 8, StopBits.Two );
		// Required for e.g. SharkLink & Yaroze cable compat. Doesn't interfere with the 3-wire setups
		activeSerial.Handshake = Handshake.None;		
		activeSerial.DtrEnable = true;
		activeSerial.RtsEnable = true;
		
		if ( fastMode ){
			
			activeSerial.ReadTimeout = TIMEOUT;
			activeSerial.WriteTimeout = TIMEOUT;

			try {
				activeSerial.Open();
			} catch( Exception exception ) {

				Console.WriteLine( "Error opening temporary serial port on " + argComPort + "!" );
				Console.WriteLine( exception.Message );

				return false;

			}

			// The bytes "FAST" with no null terminator
			activeSerial.Write( BitConverter.GetBytes( 0x54534146 ), 0, 4 );
			

			Thread.Sleep( 100 );
			

			activeSerial.Close();
			
			// We need to find a suitable overlap in frequencies which can be divided
			// from the Playstation's clock and from the FTDI/clone, with some wiggle room.
			//
			// PSX/libs uses whole integer divisions of 2073600 for anything over 115200
			// giving us 518400 close to the half-megabyte mark.
			//		
			// Most FTDI/clones seem to operate in 2xinteger divisions of 48mhz
			// giving us 510000 or 521000 close to the half-megabyte mark. e.g. (48m/47/2) or (48m/46/2)
			//
			// 5210000 (and even 518400) is a little fast, but the lower end
			// of things (510000 to 518300) seems pretty stable.
			//
			// note: psx @ 518400, pc @ 510000
			//
			activeSerial = new SerialPort( argComPort, 510000, Parity.None, 8, StopBits.Two );
			// Required for e.g. SharkLink & Yaroze cable compat. Doesn't interfere with the 3-wire setups
			activeSerial.Handshake = Handshake.None;
			activeSerial.DtrEnable = true;
			activeSerial.RtsEnable = true;

		}

		// Now the main serial port

		activeSerial.ReadTimeout = TIMEOUT;
		activeSerial.WriteTimeout = TIMEOUT;

		try {
			activeSerial.Open();
		} catch( Exception exception ) {

			Console.WriteLine( "Error opening the serial port on " + argComPort + "!" );
			Console.WriteLine( exception.Message );

			return false;

		}
			


		// just lets us skip a ton of ifs
		if( monitorComms && argCommand == CommandMode.NOT_SET ){
			TransferLogic.DoMonitor();			
			return true;
		}


		if ( usingCachedComPort ){
			
			Console.WriteLine( "Using port " + argComPort + " from comport.txt\n" );

		}
		
		// Clear the SIO buffer incase the last program has been spamming

		Console.WriteLine( "Emptying buffer... " );
		while ( activeSerial.BytesToRead != 0 ){
			Console.Write( "" + (char)activeSerial.ReadByte() );
		}
		Console.WriteLine( "...done!\n\n" );
		
		
		if ( argCommand == CommandMode.SEND_EXE ){
			
			TransferLogic.Command_SendEXE( argAddr, inFile, CalculateChecksum( inFile, true ) );

		}

		if ( argCommand == CommandMode.SEND_BIN ){

			TransferLogic.Command_SendBin( argAddr, inFile, CalculateChecksum( inFile ) );

		}
		
		// Unirom 8 mode - requires a response after checking that
		// things will fit on the cart.
		if ( argCommand == CommandMode.SEND_ROM ){

			TransferLogic.Command_SendROM(argAddr, inFile, CalculateChecksum( inFile ) );

		}


		if ( argCommand == CommandMode.RESET ){
			TransferLogic.WriteChallenge( argCommand.challenge() );
		}

		if ( argCommand == CommandMode.POKE ){
			byte[] bytes = { (byte)(argSize & 0xFF) };
			TransferLogic.Command_SendBin( argAddr, bytes, CalculateChecksum( bytes ) );
		}

		// Unirom 8.0.4 and up, enables kernel-resident SIO
		if (argCommand == CommandMode.DEBUG)
		{

			// if it returns true, we might enter /m (monitor) mode, etc
			if (
				!TransferLogic.ChallengeResponse( argCommand )
			) return false;

		}
				

		if ( argCommand == CommandMode.DUMP ){
			
			lastReadBytes = new byte[ argSize ];

			TransferLogic.Command_DumpBytes( argAddr, argSize, lastReadBytes );
			
			string fileName = "DUMP_" + argAddr.ToString( "X8" ) + "_to_" + argSize.ToString( "X8" ) + ".bin";

			if ( System.IO.File.Exists( fileName ) ){
				
				string newFilename = fileName + GetSpan().TotalSeconds.ToString();

				Console.Write( "\n\nWARNING: Filename " + fileName + " already exists! - Dumping to " + newFilename + " instead!\n\n" );
				
				fileName = newFilename;

			}

			try{
				
				File.WriteAllBytes( fileName, lastReadBytes );

			} catch ( Exception e ) {
				
				Error( "Couldn't write to the output file + " + fileName + " !\nThe error returned was: " + e, false );
				return false;
				
			}


		} // DUMP

		if (argCommand == CommandMode.PING)
		{
			TransferLogic.ChallengeResponse( argCommand );
		}

		if ( argCommand == CommandMode.GDB ){
			
			GDB.Init();

		}

		if ( argCommand == CommandMode.JUMP_JMP ){
			TransferLogic.Command_JumpAddr( argAddr );
		}

		if ( argCommand == CommandMode.JUMP_CALL ){
			TransferLogic.Command_CallAddr( argAddr );
		}

		if ( argCommand == CommandMode.HALT ){
			TransferLogic.ChallengeResponse( argCommand );
		}

		if ( argCommand == CommandMode.CONT ){
			TransferLogic.ChallengeResponse( argCommand );
		}

		if ( argCommand == CommandMode.REGS ){
			TransferLogic.Command_DumpRegs();
		}

		if ( argCommand == CommandMode.SETREG ){
			TransferLogic.Command_SetReg( argRegister, argAddr );
		}
		
		if ( argCommand == CommandMode.WATCH ){
			
			TransferLogic.Watch( argAddr, argSize );
			return true;

		} // WATCH

		if ( monitorComms ){
			TransferLogic.DoMonitor();
		} else {
			Console.WriteLine("\n This is where we part ways!");
			activeSerial.Close();			
		}

		return true;

	} // void Transfer


	// Older .NET so getter/setter instead of expression-bodied statement.
	public static bool POSIXEnvironment{ get{
		
		// 128 being mono's "Unix" ID in older versions.

		return (
			Environment.OSVersion.Platform == PlatformID.MacOSX 
			|| Environment.OSVersion.Platform == PlatformID.Unix
			|| Environment.OSVersion.Platform == (PlatformID)128
		);

	} }

	public static bool ValidPort( string inPort ){
		
		if ( POSIXEnvironment ){
		
			// Some kinda POSIXy environment
			// Allow any tty device as *nix users could be using all sorts of pipes n redirects

			return inPort.StartsWith( "/dev/tty" );
		
		} else {
		
			// A windowsy sort of affair

			return inPort.ToUpperInvariant().StartsWith( "COM" );
			
		}
	

	}

	
	public static TimeSpan GetSpan(){
		return (DateTime.UtcNow - new DateTime(1970, 1, 1)); // shortest way to represent the epoch?
	}


} // Main Class



// I've over-engineered this, haven't I?
// Big fan of compile-time constants though.
public class CommandAttribute : System.Attribute {

	public string commandText { get; set; }

	public CommandAttribute( string inCommand ) {
		commandText = inCommand;
	}

}

// e.g. the string we'll send to the PSX
public class ChallengeAttribute : System.Attribute {

	public string challenge{ get; set; }

	public ChallengeAttribute( string inString ){
		challenge = inString;
	}

}

// e.g. the string we expect to recieve from the PSX
public class ResponseAttribute : System.Attribute {
	
	public string response{ get; set; }
	
	public ResponseAttribute( string inResponse ){
		response = inResponse;
	}

}


// C# requires a return type even if we're just checking for the presence of an attribute

public class NeedsInputFileAttribute : System.Attribute { public bool NeedsInputFile() { return true; } }
public class NeedsOutputFileAttribute : System.Attribute { public bool NeedsOutputFile() { return true; } }
public class NeedsAddressAttribute : System.Attribute { public bool NeedsAddress() { return true; } }
public class NeedsSizeAttribute : System.Attribute{ public bool NeedsSize(){ return true; } }
public class NeedsRegisterAttribute : System.Attribute{ public bool NeedsRegister(){ return true; } }


public static class CommandModeExtensions {

	public static string command( this CommandMode inMode ) {

		CommandAttribute[] attributes =
			(CommandAttribute[])inMode.GetType().GetField( inMode.ToString() ).GetCustomAttributes( typeof( CommandAttribute ), false );

		if( attributes.Length == 0 ) {
			Console.WriteLine( "internal error" );
			return "";
		} else {
			return attributes[ 0 ].commandText;
		}

	}

	public static string challenge( this CommandMode inMode ) {

		ChallengeAttribute[] attributes =
			(ChallengeAttribute[])inMode.GetType().GetField( inMode.ToString() ).GetCustomAttributes( typeof( ChallengeAttribute ), false );

		if( attributes.Length == 0 ) {
			//Console.WriteLine( "internal error" );
			return "";
		} else {
			return attributes[ 0 ].challenge;
		}

	}

	public static string response( this CommandMode inMode ) {

		ResponseAttribute[] attributes =
			(ResponseAttribute[])inMode.GetType().GetField( inMode.ToString() ).GetCustomAttributes( typeof( ResponseAttribute ), false );

		if( attributes.Length == 0 ) {
			Console.WriteLine( "internal error" );
			return "";
		} else {
			return attributes[ 0 ].response;
		}

	}

	// single-line returns would be a little unreadable...
	// could merge these into a single function, but honestly, it would absolutely kill accesability

	public static bool needsInputFile( this CommandMode inMode ) {

		NeedsInputFileAttribute[] attributes =
			(NeedsInputFileAttribute[])inMode.GetType().GetField( inMode.ToString() ).GetCustomAttributes( typeof( NeedsInputFileAttribute ), false );

		return attributes.Length > 0;
	}

	public static bool needsOutputFile( this CommandMode inMode ) {

		NeedsOutputFileAttribute[] attributes =
			(NeedsOutputFileAttribute[])inMode.GetType().GetField( inMode.ToString() ).GetCustomAttributes( typeof( NeedsOutputFileAttribute ), false );

		return attributes.Length > 0;

	}

	public static bool needsAddress( this CommandMode inMode ) {

		NeedsAddressAttribute[] attributes =
			(NeedsAddressAttribute[])inMode.GetType().GetField( inMode.ToString() ).GetCustomAttributes( typeof( NeedsAddressAttribute ), false );

		return attributes.Length > 0;

	}

	public static bool needsSize( this CommandMode inMode ){
		
		NeedsSizeAttribute[] attributes =
			(NeedsSizeAttribute[])inMode.GetType().GetField( inMode.ToString() ).GetCustomAttributes( typeof( NeedsSizeAttribute), false );

		return attributes.Length > 0;

	}

	public static bool needsRegister( this CommandMode inMode ){
		
		NeedsRegisterAttribute[] attributes =
			(NeedsRegisterAttribute[])inMode.GetType().GetField( inMode.ToString() ).GetCustomAttributes( typeof( NeedsRegisterAttribute), false );

		return attributes.Length > 0;

	}

}