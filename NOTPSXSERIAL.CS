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

	[Command( "/oldexe" )]	
	[NeedsInputFile]
	[Challenge("c")]
	[Response( "ignored" )]
	SEND_OLD_EXE,

	[Command( "/oldrom" )]	
	[NeedsInputFile]
	[Challenge("r")]
	[Response( "ignored" )]
	SEND_OLD_ROM,

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

	[Command( "/watch" )]
	[Challenge("HEXD")]
	[Response("OKAY")]
	[NeedsAddress]
	[NeedsSize]
	WATCH,

	[Command( "/debug" )]
	[Challenge("DEBG")]
	[Response("OKAY")]		
	DEBUG,

	[Command( "/reset" )]
	[Challenge("REST")]
	[Response("OKAY")]	
	RESET,

	[Command( "/verify" )]
	[Challenge("SBIN")]
	[Response("OKAY")]
	[NeedsInputFile]
	[NeedsAddress]	
	VERIFY,
		

	COUNT

};


internal class Program {

	const string VERSION = "v6.Matthew";
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

		Console.Write( "    Note: If windows is adamant that your serial device (FTDI, etc) can't do\n" );
		Console.Write( "          115200baud then install mono and launch via 'mono nops.exe /args ..." );
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

		Console.Write( "    Legacy Commands:\n" );
		Console.Write( "      Send an .EXE :  NOPS.EXE /oldexe <FILE.EXE> {0}    (PSXSerial, Unirom1-7) \n", suggestedComPort );
		Console.Write( "      Flash a .ROM :  BLAH.ROM /oldrom <FILE.ROM> {0}    (Unirom7b Only) \n\n", suggestedComPort );

		Console.Write( "    Extra switches:\n" );		
		Console.Write( "      /m to open the Serial IO monitor (can be used /m {0} alone)\n", suggestedComPort );
		Console.Write( "      /fast to enable or continue using 500k baud\n" );
		Console.Write( "      /debug to enable experimental /bin & /dump during gameplay\n" );
		Console.Write( "\n" );

	}


	static string argComPort = "";
	static string suggestedComPort = "COM8";
	
	static CommandMode argCommand = CommandMode.NOT_SET;

	

	static UInt32 argAddr;
	static UInt32 argSize;
	static bool satisfiedAddressRequirements = false;
	static bool satisfiedSizeRequirements = false;

	static bool needInputFile = false;
	static bool needOutputFile = false;	
	static bool satisfiedFileRequirements = false;
	static string argFileName;
	static byte[] inFile;
	static bool fastMode = false;
	static bool ignoreChecksum = false;
	// keep it globally scoped so we can process it later
	static byte[] lastReadBytes;

	static bool monitorComms = false;
	static bool usingCachedComPort = false;
	
	static UInt32 checkSum = 0; // in or out
	

	// Quickly check that the input is vaguely sane.
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
							argAddr = ParseAddress( argCommand, argVal );
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
							argSize = ParseAddress( argCommand, argVal );
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

		if ( !argCommand.needsAddress() )
			satisfiedAddressRequirements = true;

		if ( !argCommand.needsSize() )
			satisfiedSizeRequirements = true;

		if( !satisfiedAddressRequirements ) {
			return Error( "Did you specify an address? E.g. 0x23456788\n" );
		}

		if ( !satisfiedSizeRequirements ){
			return Error( "Did you specify a size? E.g. 0x23456788\n" );
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

	static UInt32 ParseAddress( CommandMode inCommand, string inString ) {

		string iLower = inString.ToLowerInvariant();
		iLower = iLower.Replace( inCommand.command().ToLowerInvariant(), "" );

		#if DebugArgs
		Console.WriteLine( "Parsing address " + inString );
		#endif

		// Whatever's left should be the address

		UInt32 outAddr = (uint)Convert.ToInt32( iLower, 16 );

		Console.Write( " - Using addr: 0x" + outAddr.ToString( "X8" ) + "\n" );

		return outAddr;

	}

	static bool Error( string inString, bool printHeader = true ) {

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


	public static void WriteChallenge( SerialPort inPort ){
		
		inPort.Write( argCommand.challenge() );

	}

	public static bool WaitResponse( SerialPort inPort, bool verbose = true ){
		
		if ( argCommand == CommandMode.RESET ){
			// just reset
			return true;
		}

		// Dump the response into a buffer..
		// (byte by byte so we can compare the challenge/response)
		// e.g. it may start spewing data immediately after and we
		// have to catch that.
		// note: the attribute extensions use 40ish bytes of memory per pop

		string responseBuffer = "";

		if ( verbose )
			Console.WriteLine( "Waiting for response: " + argCommand.response() );

		while ( true ){
			
			if ( inPort.BytesToRead != 0 ){
				// Why the fuck does readchar return an int?
				responseBuffer += (char)inPort.ReadByte();
			}
			
			if ( verbose )
				Console.Write( "\r Response: " + responseBuffer );
			
			// command unsupported in debug mode
			if ( responseBuffer == "UNSP" ){
				return false;
			}

			if ( 
				// regular mode
				responseBuffer == argCommand.response()				
				// legacy modes
				|| ( argCommand == CommandMode.SEND_OLD_EXE && responseBuffer.Length > 0 && responseBuffer[ responseBuffer.Length -1] == 0x99 )
				|| ( argCommand == CommandMode.SEND_OLD_ROM && responseBuffer.Length > 0 && responseBuffer[ responseBuffer.Length -1] == 0x99 )
			){
				if ( verbose )
					Console.WriteLine( "\nGot response: " + responseBuffer + "!" );
				break;
			}

			// filter any noise at the start of the response
			// seems to happen once in a while
			if ( responseBuffer.Length > 4 )
				responseBuffer = responseBuffer.Remove( 0, 1 );
				

		}

		return true;

	}


	private static void Main( string[] args ) {
		
		
		if ( !VerifyArgs( args ) ){
			return;
		}

		
		if ( argCommand != CommandMode.VERIFY ){
			
			// Just normal mode

			Transfer();

		} else {
			
			VerifyTransfer();

		}

	}




	private static bool Transfer() {
		
		
		PrintUsage( true );

		// See the warning regarding 115200 compat on some FTDIs under windows
		// Personally, I've had the most luck with an arduino.

		
		SerialPort serialPort = new SerialPort( argComPort, 115200, Parity.None, 8, StopBits.Two );
		
		if ( fastMode ){
			
		
			serialPort.ReadTimeout = TIMEOUT;
			serialPort.WriteTimeout = TIMEOUT;

			try {
				serialPort.Open();
			} catch( Exception exception ) {

				Console.WriteLine( "Error opening temporary serial port on " + argComPort + "!" );
				Console.WriteLine( exception.Message );

				return false;

			}

			// The bytes "FAST" with no null terminator
			serialPort.Write( BitConverter.GetBytes( 0x54534146 ), 0, 4 );
			

			Thread.Sleep( 100 );
			

			serialPort.Close();
			
			// We need to find a suitable overlap in frequencies which can be divided
			// from the playstation's clock and from the FTDI/clone, with some wiggle room.
			//
			// PSX/libs uses whole integer divisions of 2073600 for anything over 115200
			// giving us 518400 close to the half-megabyte mark.
			//		
			// Most UARTs seem to operate in 2xinteger divisions of 48mhz
			// giving us 510000 or 521000 close to the half-megabyte mark. e.g. (48m/47/2) or (48m/46/42)
			//
			// 5210000 (and even 518400) is a little fast, but the lower end
			// of things (510000 to 518300) seems pretty stable.
						
			serialPort = new SerialPort( argComPort, 510000, Parity.None, 8, StopBits.Two );

		}

		// Now the main serial port

		serialPort.ReadTimeout = TIMEOUT;
		serialPort.WriteTimeout = TIMEOUT;

		try {
			serialPort.Open();
		} catch( Exception exception ) {

			Console.WriteLine( "Error opening the serial port on " + argComPort + "!" );
			Console.WriteLine( exception.Message );

			return false;

		}
			


		// just lets us skip a ton of ifs
		if( monitorComms && argCommand == CommandMode.NOT_SET ){
			goto doMonitor;
		}

		// pad the input file to 2048 bytes.
		// not really necessary, but let's do it.
		if( needInputFile ) {
			
			int mod = inFile.Length % 2048;

			if ( mod != 0 ){
				
				Console.WriteLine( "Padding to 2048 bytes...\n\n" );

				// memory will be freed pretty quickly, so we can leave the GC out of this.
				int paddingRequired = 2048 - mod;
				byte[] newArray = new byte[ inFile.Length + paddingRequired ];
				for( int i = 0; i < newArray.Length; i++ ){
					newArray[i] = ( i < inFile.Length ) ? inFile[i] : (byte)0;					
				}
				inFile = newArray;

			}
			
			// checksum ( fancy way of saying added all the things together )
			// start it after 2k for the .exe (as that's all we'll send)
			// just send a 0 for verify mode so the PSX doesn't stop
			checkSum = 0;
			
			if ( !ignoreChecksum ){
				for ( int i = (argCommand == CommandMode.SEND_EXE ? 0x800 : 0); i < inFile.Length; i++ ){
					checkSum += (UInt32)inFile[i];  // signed add?
				}
			}

		}

		if ( usingCachedComPort ){
			
			Console.WriteLine( "Using port " + argComPort + " from comport.txt\n" );

		}
		
		// Clear the SIO buffer incase the last program has been spamming

		Console.WriteLine( "Emptying buffer... " );
		while ( serialPort.BytesToRead != 0 ){
			Console.Write( "" + (char)serialPort.ReadByte() );
		}
		Console.WriteLine( "...done!\n\n" );
		
		
		// If we're in solo monitor mode, there will be no
		// challenge/response, let's future-proof it though

		if ( !string.IsNullOrEmpty( argCommand.challenge() ) ){
			
			// Now send the challenge code and wait
			Console.WriteLine( "Waiting for the PS1....\n\n" );

			WriteChallenge( serialPort );
		
			Thread.Sleep( 500 );


			if ( WaitResponse( serialPort ) ){
			
				Console.Write( "Success!" );

			} else {
			
				return Error( "This function is not supported while UniROM is in debug mode!\n" );

			}

		}
		

		bool skipFirstSectorHeader = false;

		
		if ( argCommand == CommandMode.SEND_EXE || argCommand == CommandMode.SEND_OLD_EXE ){
			
			// An .exe with in-tact header sends the actual header over
			// followed by some choice meta data.
			skipFirstSectorHeader = true;
			serialPort.Write( inFile, 0, 2048 );

			// Write in the header		
			serialPort.Write( inFile, 16, 4 );		// the .exe jump address
			serialPort.Write( inFile, 24, 4 );		// the base/write address, e.g. where the linker org'd it
			//serialPort.Write( inFile, 28, 4 );		// size
			// let's not use the header-defined length, instead the actual file length minus the header
			serialPort.Write( BitConverter.GetBytes( inFile.Length - 0x800 ), 0, 4 );

			// old mode doesn't use checksum
			if ( argCommand == CommandMode.SEND_EXE ){
				serialPort.Write( BitConverter.GetBytes( checkSum ), 0, 4 );
			}

			// We could send over the initial values for the fp and gp register, but 
			// GP is set via LIBSN or your Startup.s/crt0 and it's never been an issue afaik
			
		}

		if ( argCommand == CommandMode.SEND_BIN ){
			
			
			serialPort.Write( BitConverter.GetBytes( argAddr ), 0, 4 );
			serialPort.Write( BitConverter.GetBytes( inFile.Length ), 0, 4 );				
			serialPort.Write( BitConverter.GetBytes( checkSum ), 0, 4 );

			// then the actual contents.

		}

		// Unirom 4 and up, enables debug features in game
		if ( argCommand == CommandMode.DEBUG ){
			
			// challenge is handled... 

		}

		// Unirom 7b only - (no official release but floating about the forums)
		if ( argCommand == CommandMode.SEND_OLD_ROM ){
			
			// challenge is handled... upload is handled

		}

		// Unirom 8 mode - requires a response after checking that
		// things will fit on the cart.
		if ( argCommand == CommandMode.SEND_ROM ){
						
			serialPort.Write( BitConverter.GetBytes( inFile.Length ), 0, 4 );
			serialPort.Write( BitConverter.GetBytes( checkSum ), 0, 4 );

			string flashResponse = "";

			while ( true ){
			
				if ( serialPort.BytesToRead != 0 ){
					// Why the fuck does readchar return an int?
					flashResponse += (char)serialPort.ReadByte();

				}
			
				Console.Write( "\r EEPROM Check: " + flashResponse );

				if ( flashResponse == "FITS" ){					
					Console.WriteLine( "\n\nRom will fit! \n Response: " + flashResponse + "!" );
					break;
				}

				if ( flashResponse == "NOPE" ){					
					Console.WriteLine( "\n\nThis rom is too big for the EEPROM! \n Response: " + flashResponse + "!" );
					return false;
				}

				if ( flashResponse == "NONE" ){					
					Console.WriteLine( "\n\nNo EEPROM detected! \n The response was: " + flashResponse + "!" );
					return false;
				}

				if ( flashResponse == "UNKN" ){					
					Console.WriteLine( "\n\nUnknown EEPROM detected! \n The response was: " + flashResponse + "!" );
					return false;
				}
				

				// filter any noise at the start of the response
				// seems to happen once in a while
				if ( flashResponse.Length > 4 )
					flashResponse = flashResponse.Remove( 0, 1 );

			}
					
		}

		if ( argCommand == CommandMode.JUMP_JMP ){
			
			serialPort.Write( BitConverter.GetBytes( argAddr ), 0, 4 );

		}

		if ( argCommand == CommandMode.JUMP_CALL ){
			
			serialPort.Write( BitConverter.GetBytes( argAddr ), 0, 4 );

		}

		// send it over in 2kb chunks (same as CD sector size)
		if ( needInputFile ){
			
			
			// .exe files go [ header ][ meta ][ data @ write address ]
			// .rom files go [ meta ][ data @ 0x80100000 ]
			// .bin files go [ size ][ data @ 0xWRITEADR ]
			
			int start = skipFirstSectorHeader ? 2048 : 0;		// for .exes

			int chunkSize = 2048;								// 2048 seems the most stable
			int numChunks = inFile.Length / chunkSize + ( inFile.Length % chunkSize == 0 ? 0 : 1 );

			int waityCakes = 0;									// Kinda extraneous, but it's interesting to watch
			
			// we already sent the first one?
			for( int i = start; i < inFile.Length; i += chunkSize ){
				
				// Are we about to go out of range?
				// .NET doesn't care if you specify 2kb when you're only e.g. 1.7kb from the boundary
				// but it's best to declare explicityly
				if ( inFile.Length - i < chunkSize )
					chunkSize = inFile.Length -1;

				serialPort.Write( inFile, i, chunkSize );
				
				while( serialPort.BytesToWrite != 0 ){
					waityCakes++;					
				}

				Console.ForegroundColor = ConsoleColor.Green;				
				int percent = (i + 1) * 100 / ( inFile.Length );
				Console.Write( "\r Sending chunk {0} of {1} ({2})%", (i/chunkSize)+1, numChunks, percent );

				Console.ForegroundColor = ConsoleColor.White;
				
				/*
				// Removed due to complications with cp2104
				// Possible to recieve a response byte every 2kb
				while ( serialPort.BytesToRead != 0 ){
					Console.Write( (char)serialPort.ReadByte() );
				}
				*/
				
				Console.WriteLine( " OK" );

			}
			
			// might have to terminate previous line
			Console.WriteLine( "\nSend finished!\n" );

			// Doesn't look like anyone misses this for older serial comms.
			/*
			for( int j = 0; j < 2048; j++ ) {
				serialPort.Write( new byte[] { 255 }, 0, 1 );
			}
			*/
			

		} // if ( needInputFile )
		

		if ( argCommand == CommandMode.DUMP ){
			
			
			// the handshake is done, let's tell it where to start
			serialPort.Write( BitConverter.GetBytes( argAddr ), 0, 4 );
			serialPort.Write( BitConverter.GetBytes( argSize ), 0, 4 );

			// now go!
			int arrayPos = 0;			
			lastReadBytes = new byte[ argSize ];

			// Let the loop time out if something gets a bit fucky.
			TimeSpan startSpan = (DateTime.UtcNow - new DateTime(1970, 1, 1)); // shortest way to represent the epoch?
			double lastReadTime = startSpan.TotalSeconds;

			TimeSpan currentSpan = (DateTime.UtcNow - new DateTime(1970, 1, 1));
			double currentTime = currentSpan.TotalSeconds;

			checkSum = 0;

			while ( true ){
				
				currentSpan = (DateTime.UtcNow - new DateTime(1970, 1, 1));
				currentTime = currentSpan.TotalSeconds;

				if( serialPort.BytesToRead != 0 ){
				
					lastReadTime = currentTime;

					byte responseByte = (byte)serialPort.ReadByte();				// todo: why does ReadByte read an Int?
					lastReadBytes[ arrayPos ] = ( responseByte );
				
					arrayPos++;
					
					checkSum += (UInt32)responseByte;

					if ( arrayPos >= lastReadBytes.Length ){
						break;
					}
					
					if ( arrayPos  % 1000 == 0 ){
						long percent = ( arrayPos * 100 ) / argSize;
						Console.Write( "\r Offset {0} of {1} ({2})%\n", arrayPos, argSize, percent );
					}

				}

				// if we've been without data for more than 2 seconds, something's really up				
				if ( currentTime > lastReadTime + 2 ){
					if ( arrayPos == 0 ){
						Error( "There was no data for a long time! 0 bytes were read!", false );
						return false;
					} else {
						Error( "There was no data for a long time! Will try to dump the " + arrayPos + " (" + arrayPos.ToString("X8") + ") bytes that were read!", false );
					}
					
					break;
				}
				

			}

			Console.WriteLine( "Read Complete!" );

			// Read 4 more bytes for the checksum

			// Let the loop time out if something gets a bit fucky.
			startSpan = (DateTime.UtcNow - new DateTime(1970, 1, 1)); // shortest way to represent the epoch?
			lastReadTime = startSpan.TotalSeconds;
			int expectedChecksum = 0;

			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine( "Checksumming the checksums for checksummyness.\n" );
			
			try{ 

			for( int i = 0; i < 4; i++ ){
				
				while( serialPort.BytesToRead == 0 ) {
					
					currentSpan = ( DateTime.UtcNow - new DateTime( 1970, 1, 1 ) );
					currentTime = currentSpan.TotalSeconds;

					if ( currentTime > lastReadTime + 2 ){
						Console.ForegroundColor = ConsoleColor.Red;
						Console.WriteLine( "Error reading checksum byte " + i + " of 4!" );
						break;
					}

				}
				
				lastReadTime = currentTime;

				byte inByte = (byte)serialPort.ReadByte();

				// and shift it ino the expected checksum
				expectedChecksum |= ( inByte << ( i * 8 ) );

			}

			} catch ( System.TimeoutException ){
				
				Console.ForegroundColor = ConsoleColor.Red;
				Error( "No checksum sent, continuing anyway!\n ", false );

			}

			if ( expectedChecksum != checkSum ){
				Console.ForegroundColor = ConsoleColor.Red;
				Error( "Checksum missmatch! Expected: " + expectedChecksum.ToString( "X8" ) + "    Calced: %x\n" + checkSum.ToString( "X8" ), false );
				Error( " WILL ATTEMPT TO SAVE FILE ANYWAY\n", false );
			} else {
				Console.ForegroundColor = ConsoleColor.White;
				Console.WriteLine( " Checksums match: " + expectedChecksum.ToString( "X8" ) + "\n" );
			}


			if ( serialPort.BytesToRead > 0 ){
				Console.ForegroundColor = ConsoleColor.Red;
				Error( "Extra bytes still being sent from the PSX! - Will attempt to save file anyway!", false );
			}

			Console.ForegroundColor = ConsoleColor.White;

			string fileName = "DUMP_" + argAddr.ToString( "X8" ) + "_to_" + argSize.ToString( "X8" ) + ".bin";

			if ( System.IO.File.Exists( fileName ) ){
				
				string newFilename = fileName + lastReadTime.ToString();

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


		if ( argCommand == CommandMode.WATCH ){
			
			int bytesRead = 0;
			int arrayPos = 0;			
			lastReadBytes = new byte[ argSize ];

			// the handshake is done, let's tell it where to start
			arrayPos = 0;
			serialPort.Write( BitConverter.GetBytes( argAddr ), 0, 4 );			
			serialPort.Write( BitConverter.GetBytes( argSize ), 0, 4 );
			
			while( true ){
				
				// Keep reading bytes until we've got as many back as we've asked for

				if( serialPort.BytesToRead != 0 ){
					
					// still bothers me that it reads an int...
					byte responseByte = (byte)serialPort.ReadByte();
					lastReadBytes[ arrayPos ] = ( responseByte );
				
					bytesRead++;
					arrayPos++;
					
					// filled the buffer? Print it

					if ( arrayPos >= lastReadBytes.Length ){
							
						Console.Clear();
						Console.Write( "Watching address range 0x" + argAddr.ToString("X8") + " to 0x" + (argAddr+argSize).ToString("X8") +  "\n" );
						Console.Write( "Bytes read " + bytesRead + "\n\n" );

						for( int i = 0; i < lastReadBytes.Length; i++ ){
							
							Console.Write( lastReadBytes[i].ToString( "X2") + " " );

							// Such a janky way to do it, but is saves appending
							// tons and tons of strings together
							if ( i % 16 == 15 ){
								
								// print the actual char values

								for( int j = i -15; j <= i; j++ ){
									
									Console.Write( " " + (char)lastReadBytes[j] );

								}

								// then draw the character data								
								Console.Write( "\n" );

							}
							 
						}

						if ( serialPort.BytesToRead != 0 ){
							Console.Write( "\nTerminator bytes: " );
							while( serialPort.BytesToRead != 0 ){
								int x = serialPort.ReadByte();
								Console.Write( x.ToString("X2") + " " );
							}
							Console.Write( "\n" );
						}


						// slow it down a touch
						
						// give the PSX time to do stuff
						Thread.Sleep( 200 );

						// Just start over...						
						// PSX will essentially be stuck in an interrupt from here

						WriteChallenge( serialPort );
						WaitResponse( serialPort, false );
						
						// start over
						arrayPos = 0;
						serialPort.Write( BitConverter.GetBytes( argAddr ), 0, 4 );						
						serialPort.Write( BitConverter.GetBytes( argSize ), 0, 4 );
						
						/*
						// remove the challenge/response from the buffer
						for( int i = 0; i < 8; i++ ){
							while ( serialPort.BytesToRead == 0 ){
					
							}
							serialPort.ReadByte();
						}
						*/


					}
					
				} // bytestoread

			} // while true


		} // WATCH


	doMonitor:
		
		if ( !monitorComms ){
			
			Console.WriteLine( "\n This is where we part ways!" );
			serialPort.Close();
			return true;

		} else {

			while( true ){
				
				if( serialPort.BytesToRead > 0 ) {

					int responseByte = serialPort.ReadByte();				
					Console.Write( (char)( responseByte ) );
				
				}

			}
			
		}

		// Hi! The compiler says I'm unreachable
		

	} // void Transfer


	// TODO: this is so hacky
	// E.g. it uses the args supplied to SBIN (upload bin)
	// then tweaks the params and redownloads them.

	static void VerifyTransfer(){
				
			// Run a bin upload then a verify

			// would gum things up
			monitorComms = false;
			ignoreChecksum = true;
			
			// Switch to upload mode, same params
			argCommand = CommandMode.SEND_BIN;
			if ( !Transfer() ){
				return;
			}

			// PSX is about to wait a fixed time
			// let's do the same rather than interfering with SIO
			Console.Write( "Waiting for PSX...\n" );
			Thread.Sleep( 1000 );
			
			// Then switch back to dump mode
			needInputFile = false;
			argSize = (uint)inFile.Length;
			argCommand = CommandMode.DUMP;
			Transfer();

			Console.WriteLine( "InFile: " + inFile.Length + "bytes" );
			Console.WriteLine( "OutFile: " + lastReadBytes.Length + "bytes" );

			// Now verify the dump vs the original (possible patched) bytes

			int numErrors = 0;
			for( int i = 0; i <inFile.Length; i++ ){
				
				if ( inFile[i] != lastReadBytes[i] ){					
					if ( numErrors < 2000 ){
						
						// Do a little hex dump

						Console.Write( "Error at 0x" + i.ToString( "X" ) + " (0x" + ( i + argAddr).ToString("X") + "):    (expected/actual)\n" );
						
						// Could use a stringbuilder... or we could keep it readable...
						string expectedLeft = "0x" + i.ToString("X") + ": ";
						string expectedRight = "  ";
						string actualLeft = "0x" + i.ToString("X") + ": ";
						string actualRight = "  ";

						int lineStart = i - ( i % 16 );

						for( int j = lineStart; j < lineStart + 16; j++ ){

							// Highlight current or previous byte?
							if ( i == j ){
								expectedLeft += ">";
								actualLeft += ">";
							} else if ( j == i + 1 ){
								expectedLeft += "<";
								actualLeft += "<";
							} else {
								expectedLeft += " ";
								actualLeft += " ";
							}

							// Print!
							expectedLeft += inFile[ j ].ToString( "X2" );
							actualLeft += lastReadBytes[ j ].ToString( "X2" );
							expectedRight += (char)inFile[i];
							actualRight += (char)lastReadBytes[i];

						}
						
						Console.Write( expectedLeft );
						Console.Write( expectedRight + "\n\n" );

						Console.Write( actualLeft );
						Console.Write( actualRight + "\n" );
						

					}

					numErrors++;

				}

			}
			Console.Write( "Total errors: " + numErrors + "\n" );
			if ( numErrors > 2000 ){
				Console.Write( "Stopped listing after 2000 errors!\n" );
			}

			Console.Write( "NOTE: Please be aware that the area of ram you've selected may be in use!\n" );


	} //VerifyTransfer

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

}