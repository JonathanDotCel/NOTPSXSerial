using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;


class PCDrv {

    public static SerialPort serial => Program.activeSerial;

	public enum PCDrvCodes{ unknown, PCINIT_101, PCCREAT_102, PCOPEN_103, PCCLOSE_104, PCREAD_105 };

	/// <summary>
	/// Grab the filename over sio
	/// Reads up to 255 chars or till it hits a null terminator
	/// </summary>
	/// <returns></returns>
	private static string GetFilename() {

		string fileName = "";

		while ( true ) {

			if ( serial.BytesToRead > 0 ) {

				char inChar = (char)serial.ReadChar();

				if ( inChar == 0 ) {
					return fileName;
				} else {
					fileName += inChar;
				}

				if ( fileName.Length > 255 ) {
					Utils.Error( "Filename overflow!" );
					return "";
				}

			} // bytesToRead

		} // while

	} // local GetFileName

	class PCFile{

		public string fileName;
		public UInt32 handle;
		public bool hasBeenClosed = false;
		public int filePointer = 0;
		public SeekOrigin seekMode = SeekOrigin.Begin;

	}

	private static List<PCFile> activeFiles = new List<PCFile>();

	private static bool IsFileOpen( string inFile ){
		return GetOpenFile( inFile ) != null;
	}

	// Leaving these plain C style (vs linq) for readability
	private static PCFile GetOpenFile( string inFile ){
		for( int i = 0; i < activeFiles.Count; i++ ){

			// not recycling handles
			if ( activeFiles[i].hasBeenClosed ) continue;

			if ( activeFiles[i].fileName.ToLowerInvariant() == inFile.ToLowerInvariant() ){
				return activeFiles[i];
			}
		}
		return null;
	}

	private static PCFile GetOpenFile( UInt32 inHandle ){
		for ( int i = 0; i < activeFiles.Count; i++ ) {

			// not recycling handles
			if ( activeFiles[i].hasBeenClosed ) continue;

			if ( activeFiles[ i ].handle == inHandle ) {
				return activeFiles[ i ];
			}

		}
		return null;
	}

	private static bool ClosePCFile( UInt32 inHandle ){
        for( int i = 0; i < activeFiles.Count; i++ ){
			
			// not recycling handles
			if ( activeFiles[i].hasBeenClosed ) continue;

			if ( activeFiles[i].handle == inHandle ){
				activeFiles[i].hasBeenClosed = true;
				return true;
			}

		}

		Console.WriteLine( $"No active file with handle {inHandle} to delete!" );
		return false;

	}

	private static void TrackFile( string inFile, UInt32 inHandle ){

		// It's already tracked, open
		if ( GetOpenFile( inFile ) != null ) return;

		PCFile p = new PCFile(){ fileName = inFile, handle = inHandle };
		activeFiles.Add( p );

	}

	private static UInt32 NextHandle(){
		return (UInt32)activeFiles.Count +1;
	}


	/// <summary>
	/// The monitor recieved 0x00, 'p' ... 
	/// Read the command ID bytes following that and process them.
	/// </summary>
	public static bool ReadCommand(){

		Console.WriteLine( "Got PCDRV ..." );

		while ( serial.BytesToRead == 0 ) { }
		PCDrvCodes subCode = (PCDrvCodes)serial.ReadByte();

		Console.WriteLine( "Got subcode " + subCode.ToString( "X" ) );

		// TODO: split these off into discrete functions

		// PCInit
		if ( subCode == PCDrvCodes.PCINIT_101 ) {
			serial.Write( "OKAY" );
			serial.Write( new byte[]{ 0 }, 0, 1 );
		}

		// PCCreat
		if ( subCode == PCDrvCodes.PCCREAT_102 ) {

			serial.Write( "OKAY" );

			string fn = GetFilename();
			if ( fn == "" ) {
				return false;
			}
			UInt32 parameters = TransferLogic.read32();

			bool isDir = ((parameters & 16) != 0);

			Console.WriteLine( $"PCCreat( {fn}, {parameters} )" );

			PCFile f = GetOpenFile( fn );

			if ( f != null ){
				// just return the handle for this file...
				Console.WriteLine( "File already open, handle=" + f.handle );
				serial.Write( "OKAY" );
				serial.Write( BitConverter.GetBytes( f.handle ), 0, 4 );
				return true;
			}

			try {

				if ( isDir ){
					System.IO.Directory.CreateDirectory( fn );
				} else {
					if ( !File.Exists( fn ) ){
						FileStream fs = File.Create( fn );
						fs.Close();
					} else {
						Console.WriteLine( $"File {fn} already exists, using that..." );
					}
				}

				UInt32 handle = NextHandle();		
				TrackFile( fn, handle );

				serial.Write( "OKAY" );
				System.Threading.Thread.Sleep( 100 ); // TODO: __TEST__
				serial.Write( BitConverter.GetBytes( handle ), 0, 4 );
				return true;

			} catch ( Exception e ) {
				Console.WriteLine( $"Error creating file '{fn}', ex={e}" );
				serial.Write( "NOPE" );
				return false;
			}

		} // PCCREAT_102

		// PCOpen
		if ( subCode == PCDrvCodes.PCOPEN_103 ) {

			serial.Write( "OKAY" );

			string fn = GetFilename();
			if ( fn == "" ){
				return false;
			}

			UInt32 parameters = TransferLogic.read32();

			Console.WriteLine( $"PCOpen( {fn}, {parameters} )" );

			PCFile f = GetOpenFile( fn );

			if ( f != null ){

				// just return the handle for this file...
				Console.WriteLine( "File already open, handle=" + f.handle );
				serial.Write( "OKAY" );
				serial.Write( BitConverter.GetBytes( f.handle ), 0, 4 );
				return true;

			}

			if ( !File.Exists( fn ) ){
				Console.WriteLine( "File does not exist..." );
				goto nope;
			}

			try{
				FileStream fs = File.Open(fn, FileMode.Open, FileAccess.ReadWrite, FileShare.None );
				fs.Close();
			} catch( Exception e ){
				Console.WriteLine( $"Error opening file '{fn}', ex={e}" );
				goto nope;
			}
					

			UInt32 handle = NextHandle();
			TrackFile( fn, handle );
			Console.WriteLine( "Returning file, handle=" + handle );

			serial.Write( "OKAY" );
			serial.Write( BitConverter.GetBytes( handle ), 0, 4 );
			return true;


			nope:;
			Console.WriteLine( "Failed.." );
			serial.Write( "NOPE" );
			return false;

		} // PCOPEN_103

		// PCClose
		if ( subCode == PCDrvCodes.PCCLOSE_104 ){

			serial.Write( "OKAY" );

			// unirom sends extra params to save kernel
			// space by grouping similar commands together.
			UInt32 handle = TransferLogic.read32();
			UInt32 unused1 = TransferLogic.read32();
			UInt32 unused2 = TransferLogic.read32();

			Console.WriteLine( $"PCClose( {handle} ) unusedParams: {unused1},{unused2}" );

			PCFile f = GetOpenFile( handle );

			try{

				if ( f == null ) {
					Console.WriteLine( "No such file... great success!" );
				} else {
					Console.WriteLine( $"Closing file {f.fileName} with handle {f.handle}..." );					
				}
				serial.Write( "OKAY" ); // v0
				serial.Write( BitConverter.GetBytes( f.handle ), 0, 4 );   // v1

				// let the garbage collector deal with it
				ClosePCFile( f.handle );
				return true;

			} catch ( Exception e ){
				Console.WriteLine( "Error closing file..." + e );
				serial.Write( "NOPE" ); // v0
				// don't need to send v1				
				return false;
			}



		} // PCCLOSE_104

		// PCRead
		if ( subCode == PCDrvCodes.PCREAD_105 ){

			serial.Write( "OKAY" );
			// unirom sends extra params to save kernel
			// space by grouping similar commands together,
			// so 'memaddr' is for debugging only.
			UInt32 handle = TransferLogic.read32();
			int inLength = (int)TransferLogic.read32();
			UInt32 memaddr = TransferLogic.read32();

			PCFile f = GetOpenFile( handle );

			Console.WriteLine( $"PCRead( {handle}, len={inLength} ); fileName={f.fileName} MemAddr={memaddr.ToString("X")}, File={f}" );

			if ( f == null ){
				Console.WriteLine( $"No file with handle {handle}, returning!" );
				serial.Write( "NOPE" );	// v0
				// don't need to send v1
				return false;
			}

			long streamLength = 0;

			try{

				// note: PCRead would not originally stop at EOF				
				FileStream file = new FileStream( f.fileName, FileMode.Open );
				file.Seek( f.filePointer, f.seekMode );
								
				streamLength = file.Length;

				byte[] bytes = new byte[f.filePointer + inLength];
				int bytesRead = file.Read( bytes, f.filePointer, inLength );

				
				if ( bytesRead <= 0 ){
					//throw new Exception( "Read 0 bytes from the file.." );
					Console.WriteLine( "Warning - no bytes were read from the file - returning zeros..." );
				}
				
				// if we returned fewer bytes than requested, no biggie, the byte array is already set

				serial.Write( "OKAY" ); // v0
				serial.Write( BitConverter.GetBytes( bytes.Length ), 0, 4 ); // v1

				// Then
				UInt32 check = TransferLogic.CalculateChecksum( bytes, false );				
				serial.Write( BitConverter.GetBytes( check ), 0, 4 );
				TransferLogic.WriteBytes( bytes, false, true );

				// again the weird order reflects a desire to save space within the psx kernel
				// and reuse some functions

			} catch( Exception e ){
				Console.WriteLine( $"Error reading file {f.fileName} at pos={f.filePointer}, streamLength={streamLength} e={e}" );
				serial.Write( "NOPE" );
				return false;
			}


		} // PCREAD_105

		return true;

	}

}


