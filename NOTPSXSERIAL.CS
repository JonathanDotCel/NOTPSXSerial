// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Additionally, the ELFSharp binary is licensed under the terms
// of the LLVM Release license. See ELFSharp_license.txt
//
// NoPS - NotPsxSerial
// Feb 2020 - github.com/JonathanDotCel
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

// ??_09_2020 - Release 10 - (~Unirom 8.0.C)
//    - poke8, poke16, poke32 commands
//    - hookread, hookwrite, hookex

// 20_09_2020 - Release 11 - (~8.0.D)
//    - poke8, poke16, poke32 commands
//    - hookread, hookwrite, hookex
//    - /dump now has a V2 variant
//    Note: Some seriously incomplete GDB stuff in the works!

// 21_10_2020 - Spooky Release 12 - (~8.0.E)
//   - clearer errors
//   - read32 function
//   - tidied up some misused vars
//   - memory card control
//   - condensed the custom attributes
//   - some new custom attributes
//   - a wee bit more code documentation

// 05_01_2021 - Near near new nops
//  - Made things more mac/unix friendly
//  - Proper macos detection + scripts
//  - lowercased NoPS.EXE for everyone's sanity
//  - moved some things to the utils class
//

// 00_03_2020 - Release 14 - (~8.0.E)
//   - Distinction between /gdb and /bridge
//

// See github for further release info.


#define DebugArgs

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Linq;
using static Utils;

public enum CommandMode {

	NOT_SET,

	// Upload

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

	// Flow control

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
	[NeedsOutputFile]
	DUMP,

	// Poke commands

	[Command( "/poke8" )]
	[Challenge("SBIN")]
	[Response("OKAY")]
	[NeedsAddress]
	[NeedsValue]
	POKE8,

	[Command("/poke16")]
	[Challenge("SBIN")]
	[Response("OKAY")]
	[NeedsAddress]
	[NeedsValue]
	POKE16,

	[Command("/poke32")]
	[Challenge("SBIN")]
	[Response("OKAY")]
	[NeedsAddress]
	[NeedsValue]
	POKE32,

	// Peek

	[Command( "/watch" )]
	[Challenge("HEXD")]
	[Response("OKAY")]
	[NeedsAddress]
	[NeedsSize]
	WATCH,
	
	// Various

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

	// UART-TCP bridge
	[Command( "/bridge" )]
    [NeedsPort]
	[Challenge("NONE")]
	[Response("OKAY")]
	BRIDGE,

	// combines /debug and /bridge
	[Command( "/gdb" )]
    [NeedsPort]
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
	[NeedsValue]		// abused as a 32bit hexa value
	SETREG,

	// Debug hooks

	[Command( "/hookread" )]
	[NeedsAddress]
	[Challenge("HKRD")]
	[Response("OKAY")]
	HOOKREAD,

	[Command("/hookwrite")]
	[NeedsAddress]
	[Challenge("HKWR")]
	[Response("OKAY")]
	HOOKWRITE,

	[Command("/hookex")]
	[NeedsAddress]
	[Challenge("HKEX")]
	[Response("OKAY")]
	HOOKEXEC,

	// Memcard

	[Command("/mcdown")]	
	[NeedsCardNumber]
	[NeedsOutputFile]
	[Challenge("MCDN")]
	[Response("OKAY")]
	MCDOWN,

	[Command("/mcup")]
	[NeedsCardNumber]
	[NeedsInputFile]
	[Challenge("MCUP")]
	[Response("OKAY")]
	MCUP,

	[Command("/wipemem")]
	[NeedsValue]
	[NeedsAddress]
	WIPEMEM,

	[Command("/stdin")]
	[NeedsString]
	[Challenge("STDI")]
	[Response("STOK")]
	STDIN,

	[Command("/type")]
	[NeedsString]	
	TYPESTRING,
	

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

	const string VERSION = "v14 (8.0.I / 8.0.J)";
	const int TIMEOUT = 500;

	public static void PrintUsage( bool justTheTip = false ) {

		#if !DebugArgs
		if( !justTheTip ){
			Console.Clear();
		}
		#endif

		SetDefaultColour();

		// assuming 80 columns

		Console.Write( "\n" );
		Console.Write( "================================================================================\n" );
		Console.Write( "    Totally NOtPsxSerial " + VERSION + "\n" );
		Console.Write( "    Thanks: Jihad of HITMEN, Shendo, Type79, Dax, \n" );
		Console.Write( "            r0r0, Skitchin, danhans42, Schnappy, \n" );
		Console.Write( "            Nicolas Noble, T0fuZ & Arthur!\n" );
		Console.Write( "================================================================================\n" );
		Console.Write( "\n" );

		Console.Write("	Note: You may have to install mono and launch via 'mono nops.exe /args' if...\n" );
		Console.Write("      - Windows cant put your serial device (FTDI, etc) into 115200baud\n" );
		Console.Write("      - You are using a Sharklink/Net Yaroze cable\n");
		Console.Write("      - Any flavour of *nix/OSX\n");
		Console.Write( "\n\n" );

		if( justTheTip ) return;

        // COM8, /dev/tty*, /dev/cu.usbserial*, etc
        suggestedComPort = SampleCOMPortForEnvironment;
		
		Console.Write( "    Usage: NOPS.EXE [/args] FILENAME COMPORT" );
		Console.Write( "\n\n" );

		Console.Write( "      Send an .EXE :  NOPS.EXE /exe <FILE.EXE> {0}\n\n", suggestedComPort );

		Console.Write( "      Flash a .ROM :  NOPS.EXE /rom <FILE.ROM> {0}\n\n", suggestedComPort );

		Console.Write( "      Send a  .BIN :  NOPS.EXE /bin 0xADDRESS0 <FILE.BIN> {0}\n\n", suggestedComPort );	
		
		Console.Write( "      Jump addr (jr) :  NOPS.EXE /jmp 0xADDRESS0 {0}\n", suggestedComPort );
		Console.Write( "      Call addr (jal):  NOPS.EXE /jal 0xADDRESS0 {0}\n\n", suggestedComPort );
		
		Console.Write( "    Dumpy Stuff:\n" );
		Console.Write( "                RAM  (  2m) :  NOPS.EXE /dump 0x80000000 0x200000 <DUMP.BIN> {0}\n", suggestedComPort );
		Console.Write( "                ROM  (128k) :  NOPS.EXE /dump 0x1F000000 0x20000  <DUMP.BIN> {0}\n", suggestedComPort );
		Console.Write( "                ROM  (384k) :  NOPS.EXE /dump 0x1F000000 0x60000  <DUMP.BIN> {0}\n", suggestedComPort );
		Console.Write( "                ROM  (512k) :  NOPS.EXE /dump 0x1F000000 0x80000  <DUMP.BIN> {0}\n", suggestedComPort );
		Console.Write( "                BIOS (512k) :  NOPS.EXE /dump 0xBFC00000 0x80000  <DUMP.BIN> {0}\n", suggestedComPort );
		Console.Write( "                SPAD (  1k) :  NOPS.EXE /dump 0x1F800000 0x400    <DUMP.BIN> {0}\n\n", suggestedComPort );
		Console.Write( "                WARNING! If named dump file already exist it will be overwritten!\n" );
		Console.Write( "                Also note that you can use * for <DUMP.BIN> to have automagically generated filename\n\n" );

		Console.Write( "    Memory Cards:\n");
		Console.Write( "               MC0->File :  NOPS.EXE /mcdown 0 filename.mcr {0}\n", suggestedComPort);
		Console.Write( "               MC1->File :  NOPS.EXE /mcdown 1 filename.mcr {0}\n", suggestedComPort);
		Console.Write( "               File->MC0 :  NOPS.EXE /mcup 0 filename.mcr {0}\n\n", suggestedComPort);


		Console.Write( "    Pokey Poke:\n");
		Console.Write( "                 8 bits :  NOPS.exe /poke8 0x80100001 0x01 {0}\n", suggestedComPort);
		Console.Write( "                16 bits :  NOPS.exe /poke16 0x80100002 0x0202 {0}\n", suggestedComPort);
		Console.Write( "                32 bits :  NOPS.exe /poke32 0x80100004 0x04040404 {0}\n\n", suggestedComPort);

		Console.Write( "    Continuous Hex Dump (to screen):\n" );
		Console.Write( "                CD REGS:   NOPS.EXE /watch 0x1F801800 0x4 {0}\n\n", suggestedComPort );

		Console.Write( "    Extra switches:\n" );		
		Console.Write( "                /m to open the Serial IO monitor (can be used /m {0} alone)\n", suggestedComPort );
		Console.Write( "                /fast to enable or continue using 500k baud\n");
		Console.Write( "                /debug to enable experimental /bin & /dump during gameplay\n");
		Console.Write( "\n" );

		Console.Write( "    Examples:\n");
		Console.Write( "                nops /fast /rom unirom_b.rom COM14\n");
		Console.Write( "                nops /exe mything.exe /m COM14\n");
		Console.Write( "                nops /fast /poke8 0x80100000 0x04 COM14\n");
		Console.Write( "\n");

		Console.Write( "   Debug Functions:   (Must be in debug mode via L1+Square or nops /debug)\n" );
		Console.Write( "                Halt the PSX                       : nops /halt\n");
		Console.Write( "                Continue from halt/exeception/hook : nops /cont\n");
		Console.Write( "                Show registers from last interrupt : nops /regs\n");
		Console.Write( "                Set a register while halted        : nops /setreg 0xADDR 0xVALUE\n");
		Console.Write( "                Hook (halt) on memory read  *      : nops /hookread 0xADDR\n");
		Console.Write( "                Hook (halt) on memory write *      : nops /hookwrite 0xADDR\n");
		Console.Write( "                Hook (halt) on memory exec  *      : nops /hookex 0xADDR\n");
		Console.Write( "                * = Resume with nops /cont\n");
		Console.Write( "\n");

		Console.Write( "  STDIN Functions:\n" );
		Console.Write( "                Types a string into stdin  : nops /type \"something\"\n"  );
		Console.Write( "                at about 1 char per frame  : nops /type 'something'\n" );

		

		Console.Write("\n");

	}

	//
	// public stuff
	//

	public static SerialPort activeSerial;
	public static UInt32 protocolVersion = 1;

	//
	// less public stuff	
	//
    
	// Shared args
	static string argComPort = "";
	static string suggestedComPort = "COM8";
	static UInt32 argAddr;
	static UInt32 argSize;
	static UInt32 argValue;
	static UInt32 argCard;
	static UInt32 argPort;
	static string argIP;
	static CommandMode argCommand = CommandMode.NOT_SET;
	static string argFileName;
	static string argRegister = "";
	static string argString = "";
	// standalone/modifiers such as /fast, /slow, /m, etc
	static bool allArgsAreSecondary = false;		

	// Input validation
	static bool satisfiedAddressRequirements = false;
	static bool satisfiedSizeRequirements = false;
	static bool satisfiedRegisterRequirements = false;
	static bool satisfiedValueRequirements = false;
	static bool satisfiedCardRequirement = false;
	static bool satisfiedInputFileRequirements = false;
	static bool satisfiedOutputFileRequirements = false;
	static bool satisfiedIPRequirements = false;
	static bool satisfiedPortRequirements = false;
	static bool satisfiedStringRequirements = false;

	// Misc
	static byte[] inFile;
	static bool enterFastMode = false;
	static bool enterSlowMode = false;
	
	static bool monitorComms = false;
	static bool usingCachedComPort = false;
	

	/// <summary>
	/// Ensure the input args are vaguely sane
	/// - Make a list of args
	/// - Cross them off one by one as they're valid
	/// - Determine COM port
	/// - Extra args such as /monitor comms
	/// - Grab address values, filename etc		
	/// </summary>
	/// <param name="inArgs">Command line args</param>
	/// <returns>Are there any unprocessed args?</returns>
	 
	static bool VerifyArgs( string[] inArgs ) {
		
		if ( inArgs.Length == 0 ){
			PrintUsage( false );
			return false;
		}

		// Thank you, linq <3
		// We'll remove args as they're processed so they're not processed twice
		// and extra args will be left over.
		// ( we don't want extra args left over )

		List<string> remainingArgs = inArgs.ToList();

		// Specified a com port?

		#if DebugArgs
		Console.WriteLine( "__DEBUG__Argsleft: " + remainingArgs.Count );
		#endif

		// Remove the arg for the com port: COM* or /dev/tty*
		for( int i = remainingArgs.Count - 1; i >= 0; i-- ) {

			string s = remainingArgs[ i ];

			if ( ValidPort( s ) ){
				argComPort = s;
				remainingArgs.RemoveAt( i );
				break;
			}

		}

		// Cache the com port, or load the cached value
		if( string.IsNullOrEmpty( argComPort ) ) {
			
			try{
				if ( System.IO.File.Exists( "comport.txt" ) ){
					argComPort = File.ReadAllText( "comport.txt" );
				}
			} catch ( System.Exception e ){
				Console.Write( "Error checking for cached com port...\n" + e );
			}

			if ( argComPort == "" ){

                if ( !ValidPort( argComPort ) ){
                    // I think the GC can manage the hit on this one :)
                    return Error( "\nERROR! Please specify a device or COM port in the format: " + SampleCOMPortForEnvironment + "\ngot: " + argCommand ); 
                }

			} else {
				usingCachedComPort = true;
			}
			
		} else {
			
			// port was specified, cache it for alter
			try{
				File.WriteAllText( "comport.txt", argComPort );
			} catch ( System.Exception e ){
				Console.Write( "Error writing cached com port!\n" + e );
			}

		}


		// Are we using secondary args only? e.g. /fast /m
		// in this case, there's no point processing the rest of the input
		allArgsAreSecondary = true;		
		for( int i = remainingArgs.Count -1; i >= 0; i-- ){
			string lower = remainingArgs[i].ToLowerInvariant();
			if ( lower == "/m" ){
				monitorComms = true;
				remainingArgs.RemoveAt( i );
			} else if ( lower == "/fast" ){
				enterFastMode = true;
				remainingArgs.RemoveAt(i);
			} else if ( lower == "/slow" ){
				enterSlowMode = true;
				remainingArgs.RemoveAt(i);
			} else {
				allArgsAreSecondary = false;
			}
		}


#if DebugArgs
		Console.WriteLine("__DEBUG__Argsleft: " + remainingArgs.Count);
		Console.WriteLine("__DEBUG__COM: " + argComPort);
		Console.WriteLine("__DEBUG__FAST: " + enterFastMode);
		Console.WriteLine("__DEBUG__FAST: " + enterSlowMode);
#endif

		// Not really transferring anything, let's go
		if ( allArgsAreSecondary ) return true;

		// Outer loop: Does anything resemble an command? /dump, /poke, etc?
		// Specified a command arg (or several for future-proofing)?
		for ( int paramIndex = remainingArgs.Count - 1; paramIndex >= 0; paramIndex-- ) {
			
			string param = remainingArgs[ paramIndex ].ToLowerInvariant();
			
			// Inner loop: does it match any known commands?
			// If so, check for sub params
			// E.g. /dump <addr> <size> <filename>
			// first one is uninitialised... ignore it
			for( int argIndex = 1; argIndex < (int)CommandMode.COUNT; argIndex++ ) {

				CommandMode c = (CommandMode)argIndex;

				if( param.ToLowerInvariant() == c.command() ) {

					// Set the current value and remove
					// it from the list of available args
					argCommand = c;					
					remainingArgs.RemoveAt( paramIndex );

					// Now we've removed the /command, if there's
					// an address after it, it will be remaningArgs[ arg ]
					
					// Does the command require an address value?
					if( argCommand.hasAttribute<NeedsAddressAttribute>() ) {
						
						// end of the array!
						if( paramIndex >= remainingArgs.Count ) {
							return Error( "Specify an address in the format 0x01234567\n" );
						}

						#if DebugArgs
						Console.WriteLine( "__DEBUG__Command " + c + " requires an address " );
						#endif

						// reassign it						
						param = remainingArgs[ paramIndex ].ToLowerInvariant();

						// try and get the address argument
						try{
							argAddr = ParseHexa( argCommand, param );
						} catch( System.Exception ){
							return Error( "EXCEPTION: Specify an address in the format 0x01234567" );
						}

						remainingArgs.RemoveAt( paramIndex );
						satisfiedAddressRequirements = true;

					}

					// Do we need a register?
					if (argCommand.hasAttribute<NeedsRegisterAttribute>())
					{

						#if DebugArgs
						Console.WriteLine("__DEBUG__Command " + c + " requires a register ");
						#endif

						// reassign it
						param = remainingArgs[paramIndex].ToLowerInvariant();

						// TODO: verify register validity?
						// TODO: settle on a format: R4 vs V0 vs $V0, etc
						argRegister = param;

						remainingArgs.RemoveAt(paramIndex);
						satisfiedRegisterRequirements = true;

					}

					// On top of that... do we need a size? E.g. for dumping bios.
					if ( argCommand.hasAttribute<NeedsSizeAttribute>() ){
						
						#if DebugArgs
						Console.WriteLine( "__DEBUG__Command " + c + " requires a size parameter " );
						#endif

						// reassign it
						param = remainingArgs[ paramIndex ].ToLowerInvariant();

						// try to get the size argument
						try{
							argSize = ParseHexa( argCommand, param );
						} catch ( System.Exception ){
							return Error( "EXCEPTION: Specify a size in the format 0x01234567" );
						}

						remainingArgs.RemoveAt( paramIndex );
						satisfiedSizeRequirements = true;

					}

					// Do we need a value? 1 - 4 bytes
					if (argCommand.hasAttribute<NeedsValueAttribute>())
					{

						#if DebugArgs
						Console.WriteLine("__DEBUG__Command " + c + " requires a value parameter ");
						#endif

						// reassign it
						param = remainingArgs[paramIndex].ToLowerInvariant();

						// try to get the size argument
						try{
							argValue = ParseHexa(argCommand, param);							
						} catch (System.Exception){
							return Error("EXCEPTION: Specify a 1, 2 or 4 byte hex value");
						}

						remainingArgs.RemoveAt(paramIndex);
						satisfiedValueRequirements = true;

					}

                    if ( argCommand.hasAttribute<NeedsIPAttribute>() ){

                        #if DebugArgs
                        Console.WriteLine( "__DEBUG__Command " + c + " requires an IP/host parameter!" );
                        #endif

                        //param = remainingArgs[paramIndex].ToLowerInvariant();
                        argIP = param;
                        remainingArgs.RemoveAt( paramIndex );
                        satisfiedIPRequirements = true;

                    }

                    if ( argCommand.hasAttribute<NeedsPortAttribute>() ){

                        #if DebugArgs
                        Console.WriteLine( "__DEBUG__Command " + c + " requires a local/remote port!" );
                        #endif

                        if ( remainingArgs.Count == 0 ){
                            return Error( "Need to specify a local TCP/IP port!" );
                        }
                        
                        param = remainingArgs[paramIndex].ToLowerInvariant();

                        if ( !UInt32.TryParse( param, out argPort ) ){
                            return Error( "EXCEPTION: Couldn't parse a valid port number from \"" + param + "\"" );
                        }

                        remainingArgs.RemoveAt( paramIndex );
                        satisfiedPortRequirements = true;

                    }

					if ( argCommand.hasAttribute<NeedsStringAttribute>() ){

						#if DebugArgs
						Console.WriteLine("__DEBUG__Command " + c + " requires a memcard parameter ");
						#endif

						argString = remainingArgs[paramIndex];

						remainingArgs.RemoveAt( paramIndex );
						satisfiedStringRequirements = true;

					}

					// Do we need a memcard number
					if (argCommand.hasAttribute<NeedsCardNumberAttribute>())
					{

						#if DebugArgs
						Console.WriteLine("__DEBUG__Command " + c + " requires a memcard parameter ");
						#endif

						// reassign it
						param = remainingArgs[paramIndex].ToLowerInvariant();

						argCard = UInt32.Parse(param);

						/*
						// Working on something interesting here... hold tight.
						if ( _arg != "0" && _arg != "1" ){
							return Error( "EXCEPTION: memory card should be 0 or 1!" );
						} else {
							argCard = UInt32.Parse( _arg );
						}
						*/

						remainingArgs.RemoveAt( paramIndex );
						satisfiedCardRequirement = true;

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
		Console.WriteLine("__DEBUG__VALUE: " + argValue.ToString("X8"));		
		#endif

		if ( argCommand == CommandMode.NOT_SET ) {
			return Error( "Please specify a command - e.g. /r, /e /b, etc!\n\n" );
		}


		// Do we need to load an .exe, .rom, data etc?
		if ( argCommand.hasAttribute<NeedsInputFileAttribute>() ){

			// One of the args specifies a file?
			for (int i = remainingArgs.Count - 1; i >= 0; i--){
				
				string fName = remainingArgs[i];
				argFileName = fName;

				try{
					inFile = File.ReadAllBytes(fName);
				} catch ( System.Exception e ) {
					return Error("Couldn't open input file " + fName + " Exception: " + e);
				}

				satisfiedInputFileRequirements = true;
				remainingArgs.RemoveAt(i);

			}

		}
		
		// Do we need to save somewhere?
		if ( argCommand.hasAttribute<NeedsOutputFileAttribute>() ){

			// One of the args specifies a file?
			for (int i = remainingArgs.Count - 1; i >= 0; i--)
			{
				argFileName = remainingArgs[i];
				remainingArgs.RemoveAt(i);
				satisfiedOutputFileRequirements = true;
			}

		}

		// The /poke command for example doesn't require a filename
		// so that requirement is considered satisfied.
		if ( !argCommand.hasAttribute<NeedsRegisterAttribute>() ) satisfiedRegisterRequirements = true;
		if ( !argCommand.hasAttribute<NeedsAddressAttribute>() ) satisfiedAddressRequirements = true;
		if ( !argCommand.hasAttribute<NeedsSizeAttribute>() )	satisfiedSizeRequirements = true;
		if ( !argCommand.hasAttribute<NeedsValueAttribute>() ) satisfiedValueRequirements = true;
		if ( !argCommand.hasAttribute<NeedsCardNumberAttribute>() ) satisfiedCardRequirement = true;
		if ( !argCommand.hasAttribute<NeedsInputFileAttribute>() ) satisfiedInputFileRequirements = true;
		if ( !argCommand.hasAttribute<NeedsOutputFileAttribute>() ) satisfiedOutputFileRequirements = true;
        if ( !argCommand.hasAttribute<NeedsIPAttribute>() ) satisfiedIPRequirements = true;
        if ( !argCommand.hasAttribute<NeedsPortAttribute>() ) satisfiedPortRequirements = true;
		if ( !argCommand.hasAttribute<NeedsStringAttribute>() ) satisfiedStringRequirements = true;
		
		// Missed something...
		if ( !satisfiedAddressRequirements )    return Error("Did you specify an address or hex value? E.g. 0x23456788\n");
		if ( !satisfiedSizeRequirements )       return Error("Did you specify a size? E.g. 0x23456788\n");
		if ( !satisfiedRegisterRequirements )   return Error("Did you specify a register? E.g. a0");
		if ( !satisfiedValueRequirements )      return Error("Specify a 1-4 byte value in the format 0x01");
		if ( !satisfiedCardRequirement )        return Error("Specify memory card 0 or 1!");
		if ( !satisfiedInputFileRequirements )  return Error("Specify an input file!");
		if ( !satisfiedOutputFileRequirements ) return Error("Specify a filename to write to!");
		if ( !satisfiedIPRequirements )         return Error("Specify a remote host/ip!" );
		if ( !satisfiedPortRequirements )       return Error("Specify a local or remote port!" );
		if ( !satisfiedStringRequirements )     return Error("Need a string with either \"\" or ''!" );


		#if DebugArgs
		Console.WriteLine( "__DEBUG__Argsleft: " + remainingArgs.Count );
		Console.WriteLine( "__DEBUG__FILENAME: " + argFileName );
		Console.WriteLine( "__DEBUG__INPUTFILE: " + inFile );
		Console.WriteLine( "__DEBUG__stringArg: " + argString );
		#endif

		// there shouldn't be any arguments left!
		// (noise on the console, etc)
		if( remainingArgs.Count > 0 ){
			
			for( int i = remainingArgs.Count -1; i >= 0; i-- ){
				
				Error( "Unknown arg! " + remainingArgs[i] );

			}	

			return false;

		}

		// All done
		return true;

	} //VerifyArgs





	// Quite satisfyingly small
	private static void Main( string[] args ){
		
		// Verify all the required args are present and parsed properly
		if ( !VerifyArgs( args ) ){
			return;
		}

		ApplyConsoleMode();

		// Do stuff about it.
		DoStuff();

	}


	public enum SIOSPEED { SLOW, FAST }

	/// <summary>
	/// Set up a new serial connection on the port we parsed earlier
	/// </summary>
	/// <param name="inSpeed">Go fast or not go fast</param>	
	private static bool NewSIO( SIOSPEED inSpeed ){

		if ( activeSerial != null ) activeSerial.Close();

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
		int baud = (inSpeed == SIOSPEED.FAST ? 510000 : 115200);
		activeSerial = new SerialPort(argComPort, baud, Parity.None, 8, StopBits.Two);
		// Required for e.g. SharkLink & Yaroze cable compat. Doesn't interfere with the 3-wire setups
		activeSerial.Handshake = Handshake.None;
		activeSerial.DtrEnable = true;
		activeSerial.RtsEnable = true;
		activeSerial.ReadTimeout = TIMEOUT;
		activeSerial.WriteTimeout = TIMEOUT;

		try{
			activeSerial.Open();
		} catch( System.Exception exception ){
			Console.WriteLine("Error opening temporary serial port on " + argComPort + " speed=" + inSpeed + "!");
			Console.WriteLine( exception.Message );
			return false;
		}

		return true;

	}


	/// <summary>
	/// We've parsed everything from stdin, let's start processing it.
	/// </summary>
	/// <returns>success?</returns>
	private static bool DoStuff() {
		
		PrintUsage( true );

		//
		// Process the secondary/standalone commands
		// /fast, /slow, /m, etc
		//

		if ( enterFastMode ){
			
			// start in normal mode and send a "FAST" command
			// unirom doesn't have to respond, so if it's already in fast mode, no biggie
			if ( !NewSIO( SIOSPEED.SLOW ) ) return false;

			// The bytes "FAST" with no null terminator
			activeSerial.Write(BitConverter.GetBytes(0x54534146), 0, 4);
			Thread.Sleep(100);

			// Switch to fast
			if ( !NewSIO( SIOSPEED.FAST ) ) return false;

		} else if ( enterSlowMode ){

			// As above, but returns to 115200 so you don't have to 'nops /fast' all the time		

			// going to assume Unirom's in fast mode.. so start there and negotiate back down
			if ( !NewSIO( SIOSPEED.FAST ) ) return false;

			// The bytes "FAST" with no null terminator
			activeSerial.Write(BitConverter.GetBytes(0x574F4C53), 0, 4);
			Thread.Sleep( 100 );

			// Now switch back
			if ( !NewSIO(SIOSPEED.SLOW) ) return false;

		} else {

			if ( !NewSIO( SIOSPEED.SLOW ) ) return false;	

		}


		// If we got /m on its own
		if ( monitorComms && argCommand == CommandMode.NOT_SET )
		{
			GDB.MonitorSerialToSocket();			
			return true;
		}

		// we just did 'nops /fast /m' or something
		if ( allArgsAreSecondary ) return true;


		//
		// A little cleanup...
		//

		if ( usingCachedComPort )
		{			
			Console.WriteLine( "Using port " + argComPort + " from comport.txt\n" );
		}
		
		// Clear the SIO buffer incase the last program has been spamming

		Console.WriteLine( "Emptying buffer... " );
		while ( activeSerial.BytesToRead != 0 ){
			Console.Write( "" + (char)activeSerial.ReadByte() );
		}
		Console.WriteLine( "...done!\n\n" );


		//
		// Process the primary commands
		//

		// Upload

		if ( argCommand == CommandMode.SEND_EXE )
		{			
			TransferLogic.Command_SendEXE(inFile);
		}

		if ( argCommand == CommandMode.SEND_BIN )
		{
			TransferLogic.Command_SendBin(argAddr, inFile);
		}
		
		if ( argCommand == CommandMode.SEND_ROM )
		{
			TransferLogic.Command_SendROM(argAddr, inFile);
		}

		// Program flow

		if (argCommand == CommandMode.JUMP_JMP)
		{
			TransferLogic.Command_JumpAddr(argAddr);
		}

		if (argCommand == CommandMode.JUMP_CALL)
		{
			TransferLogic.Command_CallAddr(argAddr);
		}
		
		// Pokeypoke

		if ( argCommand == CommandMode.POKE8 )
		{
			byte[] bytes = { (byte)(argValue & 0xFF) };
			TransferLogic.Command_SendBin(argAddr, bytes);
		}

		if (argCommand == CommandMode.POKE16)
		{			
			short shorty = (byte)(argValue & 0xFFFF);
			byte[] bytes = BitConverter.GetBytes( shorty );
			TransferLogic.Command_SendBin(argAddr, bytes);
		}

		if (argCommand == CommandMode.POKE32)
		{	
			byte[] bytes = BitConverter.GetBytes(argValue);
			TransferLogic.Command_SendBin(argAddr, bytes);
		}

		// Utility

		if (argCommand == CommandMode.PING)
		{
			TransferLogic.ChallengeResponse( argCommand );
		}

		if (argCommand == CommandMode.RESET)
		{
			TransferLogic.WriteChallenge(argCommand.challenge());
		}

		// Debug functions

		if (argCommand == CommandMode.DEBUG)
		{

			// if it returns true, we might enter /m (monitor) mode, etc
			if (
				!TransferLogic.ChallengeResponse(argCommand)
			) return false;

		}

		if ( argCommand == CommandMode.HALT )
		{
			TransferLogic.ChallengeResponse( argCommand );
		}

		if ( argCommand == CommandMode.CONT )
		{
			TransferLogic.ChallengeResponse( argCommand );
		}

		if ( argCommand == CommandMode.REGS )
		{
			TransferLogic.Command_DumpRegs();
		}

		if ( argCommand == CommandMode.SETREG )
		{
			if ( !TransferLogic.Command_SetReg( argRegister, argValue ) ){
				return Error( "Couldn't set reg " + argRegister + " to " + argValue, false );
			}
			
		}


		// Hook functions

		if ( 
			argCommand == CommandMode.HOOKREAD 
			|| argCommand == CommandMode.HOOKWRITE 
			|| argCommand == CommandMode.HOOKEXEC 
		){
			if ( TransferLogic.ChallengeResponse( argCommand ) )
				activeSerial.Write(BitConverter.GetBytes(argAddr), 0, 4);
		}

		// Memory card 

		if ( argCommand == CommandMode.MCUP ){

			if ( !TransferLogic.Command_MemcardUpload( argCard, inFile ) ) return false;
		}
		
		if ( argCommand == CommandMode.WIPEMEM ){

			if ( !TransferLogic.Command_WipeMem( argAddr, argValue ) ) return false;
		}
		
		if (argCommand == CommandMode.MCDOWN)
		{			
			if ( !TransferLogic.Command_MemcardDownload( argCard, argFileName ) ) 
				return false;
		}

		// Peek / Read functions

		if (argCommand == CommandMode.DUMP)
		{
			if ( !TransferLogic.Command_Dump( argAddr, argSize, argFileName ) )
				return false;

		}

		if ( argCommand == CommandMode.WATCH ){
			
			TransferLogic.Watch( argAddr, argSize );
			return true;

		}

		//
		// STDIN / RAW
		//

		if ( argCommand == CommandMode.TYPESTRING )
		{

			// the sio HW only has 4 bytes available
			// You could work out a specification and pre-send the
			// expected text length with checksums, etc...
			// or you cold type at a speed that gives you
			// the chance to catch a few key strokes without
			// entering a tight while loop.
			Console.WriteLine( "Writing raw string: " + argString );
			byte[] bytes = System.Text.Encoding.ASCII.GetBytes( argString );

			for( int i = 0; i < bytes.Length; i++ ){
				activeSerial.Write( bytes, i, 1 );
				Thread.Sleep( 20 ); // 50fps to account for pal
			}
			
		}

		//
		// Major work in progress
		//

		if (argCommand == CommandMode.GDB)
		{            
			GDB.DebugInit( argPort );
		}

		if (argCommand == CommandMode.BRIDGE)
		{            
			GDB.Init( argPort );
		}

		//
		// All done, are we leaving the comms monitor open?
		//

		if ( monitorComms ){			
			GDB.MonitorSerialToSocket();
		} else {
			Console.WriteLine("\n Bye!");
			activeSerial.Close();			
		}

		return true;

	} // void Transfer

    
    /// <summary>
    /// A sample (for the help menu) of how the port should be written
    /// on this specific operating system.
    /// </summary>
    // Older .NET so using getter/setter instead of expression-bodied statements.
    public static string SampleCOMPortForEnvironment{ get{

        switch( realPlatform ){

            // windows/defaults
            default: 
                return "COM8";
            // most mac drivers seem to default to this
            case PlatformID.MacOSX: 
                //return "/dev/cu.usbserial-********";            
                //return "(/dev/cu.usbserial-******** or /dev/tty.SLAB_USBtoUART";
                return "(/dev/cu.usb* or /dev/tty.SLAB_USBtoUART";
            // mono's (old) unofficial "Unix" ID
            case (PlatformID)128: 
            case PlatformID.Unix:
                return "/dev/ttyUSB0";
        }

    }}


	/// <summary>
	/// Verify that the port argument is (potentially) valid
	/// </summary>
	/// <param name="inPort">COM5, /dev/ttyUSB0, etc</param>
	/// <returns>Does itlook potentially valid?</returns>
	public static bool ValidPort( string inPort ){
		
        switch( realPlatform ){

            // A windowsy sort of affair
            default: 
                return inPort.ToUpperInvariant().StartsWith( "COM" );
            
            // Mac
            case PlatformID.MacOSX:                 
                return inPort.ToLowerInvariant().StartsWith( "/dev/cu" ) | inPort.ToLowerInvariant().StartsWith( "/dev/tty" );   

            // Some kinda POSIXy environment
            // Allow any tty device as *nix users could be using all sorts of pipes n redirects
            case (PlatformID)128: 
            case PlatformID.Unix:
                return inPort.ToLowerInvariant().StartsWith( "/dev/tty" );
            
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
public class NeedsRegisterAttribute : System.Attribute{ public bool NeedsRegister(){ return true; } }
public class NeedsValueAttribute : System.Attribute{ public bool NeedsValue(){ return true; } }
public class NeedsCardNumberAttribute : System.Attribute{ public bool NeedsCardNumber(){ return true; } }
public class NeedsPortAttribute : System.Attribute{ public bool NeedsPort(){ return true; } }
public class NeedsIPAttribute : System.Attribute{ public bool NeedsIP(){ return true; } }
public class NeedsStringAttribute : System.Attribute{ public bool NeedsString(){ return true; } }

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
		
	public static bool hasAttribute<T>( this CommandMode inMode ) where T: class{
		
		object[] attribs = inMode.GetType().GetField( inMode.ToString() ).GetCustomAttributes( true );
		for( int i = 0; i < attribs.Length; i++ ){
			if ( attribs[i].GetType() == typeof(T) ) return true;
		}
		return false;

	}

}
