using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;


class PCDrv {

    public static SerialPort serial => Program.activeSerial;

	public enum PCDrvCodes{ unknown, PCINIT_101 = 0x101, PCCREAT_102 = 0x102, PCOPEN_103 = 0x103, PCCLOSE_104 = 0x104, PCREAD_105 = 0x105, PCWRITE_106 = 0x106, PCSEEK_107 = 0x107 };

	public enum PCFileMode{ READONLY, WRITEONLY, READWRITE };

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

	/// <summary>
	/// To keep track of the stuff we've opened, closed, etc
	/// </summary>
	class PCFile{

		public FileStream fileStream;
		public string fileName;
		public UInt32 handle;
		public bool hasBeenClosed = false;

		// Much cleaner handling this here than in the .NET FileStreams
		public PCFileMode fileMode = PCFileMode.READWRITE;

        public override string ToString() {
            return $"PCFile: name={fileName}, handle={handle}, hasClosed={hasBeenClosed}, mode={fileMode}";
        }

    }


	private static List<PCFile> activeFiles = new List<PCFile>();

	/// <summary>
	/// Dumps some info related to known/tracked files
	/// </summary>	
	public static void DumpTrackedFiles() {
		Console.WriteLine( "Tracked files: " );
		for ( int i = 0; i < activeFiles.Count; i++ ) {
			if ( activeFiles[ i ] != null ) Console.WriteLine( $"File {i} = {activeFiles[ i ]}" );
		}
	}

	/// <summary>
	/// Get a tracked file by filename,
	/// excludes files we've closed.
	/// </summary>
	/// <param name="inFile">the path</param>
	/// <returns>a <see cref="PCFile"/> reference</returns>
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

	/// <summary>
	/// Get a tracked file by handle
	/// excludes files we've closed
	/// </summary>
	/// <param name="inHandle">a file opened via PCOpen/PCCreat</param>
	/// <returns>a <see cref="PCFile"/> reference</returns>
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

	/// <summary>
	/// Close the file stream and mark it closed
	/// handles are consecutive and will not be recycled
	/// </summary>
	/// <param name="inHandle"></param>
	/// <returns></returns>
	private static bool ClosePCFile( UInt32 inHandle ){
        for( int i = 0; i < activeFiles.Count; i++ ){
			
			// not recycling handles
			if ( activeFiles[i].hasBeenClosed ) continue;

			if ( activeFiles[i].handle == inHandle ){
				activeFiles[i].hasBeenClosed = true;
				activeFiles[i].fileStream.Close();
				activeFiles[i].fileStream.Dispose();
				return true;
			}

		}

		Console.WriteLine( $"No active file with handle {inHandle} to close!" );
		return false;

	}

	/// <summary>
	/// Connect a file handle, filename and file stream
	/// </summary>
	/// <param name="inFile">the file name</param>
	/// <param name="inHandle">the handle</param>
	/// <param name="inStream">the stream opened via PCCreate/PCOpen"/></param>
	private static void TrackFile( string inFile, UInt32 inHandle, FileStream inStream, PCFileMode inMode ){

		// It's already tracked, open
		if ( GetOpenFile( inFile ) != null ) return;

		Console.WriteLine( $"Assigned file {inFile} with handle {inHandle}..." );

		PCFile p = new PCFile(){ 
			fileName = inFile, 
			handle = inHandle, 
			fileStream = inStream,
			fileMode = inMode
		};
		activeFiles.Add( p );

	}

	/// <summary>
	/// Grab a sequential handle, 1-indexed
	/// </summary>
	/// <returns>the next free handle</returns>
	private static UInt32 NextHandle(){
		return (UInt32)activeFiles.Count +1;
	}

	
	/// <summary>
	/// The monitor recieved 0x00, 'p' ... 
	/// Read the command ID bytes following that and process them.
	/// </summary>
	public static bool ReadCommand(){

		Console.WriteLine( "Got PCDRV ..." );

		// Wait till we get the PCDrv function code
		while ( serial.BytesToRead == 0 ) { }
		PCDrvCodes funcCode = (PCDrvCodes)TransferLogic.read32();

		Console.WriteLine( "Got function code: " + funcCode );

		// TODO: split these off into discrete functions?

		// PCInit
		if ( funcCode == PCDrvCodes.PCINIT_101 ) {
			serial.Write( "OKAY" );
			serial.Write( new byte[]{ 0 }, 0, 1 );
			return true;
		}

		// PCCreat
		if ( funcCode == PCDrvCodes.PCCREAT_102 ) {

			// tell unirom to start with the filename, etc
			serial.Write( "OKAY" );

			string fileName = GetFilename();
			if ( fileName == "" ) {
				return false;
			}
			UInt32 parameters = TransferLogic.read32();

			bool isDir = ((parameters & 16) != 0);

			Console.WriteLine( $"PCCreat( {fileName}, {parameters} )" );

			PCFile pcFile = GetOpenFile( fileName );

			if ( pcFile != null ){
				// We're already tracking this file, just return it's handle
				Console.WriteLine( "File already open, handle=" + pcFile.handle );
				serial.Write( "OKAY" );
				serial.Write( BitConverter.GetBytes( pcFile.handle ), 0, 4 );
				return true;
			}

			FileStream fStream;
			try {

				if ( isDir ){
					throw new Exception( "Directories are not supported!" );
				} else {
					if ( !File.Exists( fileName ) ){
						FileStream tempStream = File.Create( fileName );
						tempStream.Flush();
						tempStream.Close();
						tempStream.Dispose();
					} else {
						Console.WriteLine( $"File {fileName} already exists, using that..." );
					}
					fStream = new FileStream( fileName, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite );
				}

				// open it read/write until otherwise specified via PCOpen
				UInt32 handle = NextHandle();		
				TrackFile( fileName, handle, fStream, PCFileMode.READWRITE );
				
				serial.Write( "OKAY" );				
				serial.Write( BitConverter.GetBytes( handle ), 0, 4 );
				return true;

			} catch ( Exception e ) {
				Console.WriteLine( $"Error creating file '{fileName}', ex={e}" );
				serial.Write( "NOPE" );
				return false;
			}

		} // PCCREAT_102

		// PCOpen
		if ( funcCode == PCDrvCodes.PCOPEN_103 ) {

			serial.Write( "OKAY" );

			string fileName = GetFilename();
			if ( fileName == "" ){
				return false;
			}

			PCFileMode fileModeParams = (PCFileMode)TransferLogic.read32();

			Console.WriteLine( $"PCOpen( {fileName}, {fileModeParams} )" );

			PCFile f = GetOpenFile( fileName );

			if ( f != null ){

				// just return the handle for this file...
				
				if ( f.fileMode != fileModeParams ){
					Console.WriteLine( $"File {f.handle} already open, switching params to {fileModeParams}" );
					f.fileMode = fileModeParams;
				} else {
					Console.WriteLine( "File already open, handle=" + f.handle );
				}

				serial.Write( "OKAY" );
				serial.Write( BitConverter.GetBytes( f.handle ), 0, 4 );
				return true;

			}

			if ( !File.Exists( fileName ) ){
				Console.WriteLine( "File doesn't exist!" );
				goto nope;
			}

			FileStream fs = null;
			try{
				fs = File.Open(fileName, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite );
			} catch( Exception e ){
				Console.WriteLine( $"Error opening file '{fileName}', ex={e}" );
				goto nope;
			}

			UInt32 handle = NextHandle();
			TrackFile( fileName, handle, fs, fileModeParams );
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
		if ( funcCode == PCDrvCodes.PCCLOSE_104 ){

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
					serial.Write( "OKAY" ); // v0
					serial.Write( BitConverter.GetBytes( 0 ), 0, 4 );   // v1
					return true;
				} else {
					Console.WriteLine( $"Closing file {f.fileName} with handle {f.handle}..." );
					serial.Write( "OKAY" ); // v0
					serial.Write( BitConverter.GetBytes( f.handle ), 0, 4 );   // v1																			   // let the garbage collector deal with it
					ClosePCFile( f.handle );
					return true;
				}

			} catch ( Exception e ){
				Console.WriteLine( "Error closing file..." + e );
				serial.Write( "NOPE" ); // v0
				// don't need to send v1				
				return false;
			}



		} // PCCLOSE_104



		// PCRead
		if ( funcCode == PCDrvCodes.PCREAD_105 ){

			serial.Write( "OKAY" );

			// unirom sends extra params to save kernel
			// space by grouping similar commands together,
			// so 'memaddr' is for debugging only.

			// PCRead() takes (handle, buff*, len )
			// but interally passes it to _SN_Read as
			// ( 0, handle, len, buff* ), essentially
			// shuffling regs A0,A1,A2 into A1,A3,A2.
			// or just ( handle, len, buff* ).  lol.

			UInt32 handle = TransferLogic.read32();
			int inLength = (int)TransferLogic.read32();
			UInt32 memaddr = TransferLogic.read32();        // not used, debugging only

			//Console.WriteLine( $"PCRead( {handle}, len={inLength}, dbg=0x{memaddr.ToString("X")} )" );

			PCFile pcFile = GetOpenFile( handle );

			Console.WriteLine( $"PCRead( {handle}, len=0x{inLength.ToString("X")} ); MemAddr=0x{memaddr.ToString("X")}, File={pcFile}" );

			if ( pcFile == null ){
				Console.WriteLine( $"No file with handle 0x{handle.ToString("X")}, returning!" );
				serial.Write( "NOPE" );	// v0
				// don't need to send v1
				return false;
			} else {
				Console.WriteLine( "Reading file " + pcFile.fileName );
			}

			long streamLength = 0;
			
			try{

				FileStream fs = pcFile.fileStream;				
				
				streamLength = fs.Length;

				byte[] bytes = new byte[inLength];
				int bytesRead = fs.Read( bytes, 0, inLength );

				
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
				Console.WriteLine( $"Error reading file {pcFile.fileName} at pos={pcFile.fileStream.Position}, streamLength={streamLength} e={e}" );
				serial.Write( "NOPE" );
				return false;
			}


		} // PCREAD_105

		// PCWrite
		if ( funcCode == PCDrvCodes.PCWRITE_106 ){

			serial.Write( "OKAY" );

			// PCWrite() takes (handle, buff*, len )
			// but interally passes it to _SN_Write as
			// ( 0, handle, len, buff* ), essentially
			// shuffling regs A0,A1,A2 into A1,A3,A2.
			// or just ( handle, len, buff* ).  lol.

			UInt32 handle = TransferLogic.read32();
			int inLength = (int)TransferLogic.read32();
			UInt32 memaddr = TransferLogic.read32();        // not used, debugging only

			PCFile pcFile = GetOpenFile( handle );
						
			if ( pcFile == null ) {
				Console.WriteLine( $"No file with handle 0x{handle.ToString("X")}, returning!" );
				serial.Write( "NOPE" ); // v0
										// don't need to send v1
				return false;
			}

			Console.WriteLine( $"PCWrite( {handle}, len={inLength} ); fileName={pcFile.fileName} SourceAddr={memaddr.ToString( "X" )}, File={pcFile}" );

			if ( pcFile.fileMode == PCFileMode.READONLY ){
				Console.WriteLine( "Error: File is readonly!" );
				serial.Write( "NOPE" );
				return false;
			}
			serial.Write( "OKAY" );

			try {
						
				FileStream fs = pcFile.fileStream;
				
				byte[] bytes = new byte[inLength];
				bool didRead = TransferLogic.ReadBytes_Raw( (UInt32)inLength, bytes );

				if ( !didRead ){
					throw new Exception( "there was an error reading the stream from the psx!" );
				}

				Console.WriteLine( $"Read {inLength} bytes, flushing to {pcFile.fileName}..." );

				fs.Write( bytes, 0, inLength );
				fs.Flush( true );
				
				serial.Write( "OKAY" ); // v0
				serial.Write( BitConverter.GetBytes( bytes.Length ), 0, 4 ); // v1

				// again the weird order reflects a desire to save space within the psx kernel
				// and reuse some functions

			} catch ( Exception e ) {
				Console.WriteLine( $"Error writing file {pcFile.fileName}, streamLength={inLength} e={e}" );
				serial.Write( "NOPE" );
				return false;
			}

			return true;

		} //PCWRITE 106

		if ( funcCode == PCDrvCodes.PCSEEK_107 ){

			serial.Write( "OKAY" );
						
			UInt32 handle = TransferLogic.read32();
			int seekPos = (int)TransferLogic.read32();
			SeekOrigin seekOrigin = (SeekOrigin)TransferLogic.read32();

			PCFile pcFile = GetOpenFile( handle );

			Console.WriteLine( $"PCSeek file {handle} to {seekPos}, type={seekOrigin}, fileName={pcFile.fileStream}" );

			if ( pcFile == null ){
				throw new Exception( "There is no file with handle 0x" + handle.ToString("X") );
			}

			// Let's actually open the file and seek it to see if we bump into any issues
			try {

				FileStream fs = pcFile.fileStream;
				//fs.Seek( pcFile.filePointer, pcFile.seekMode );
				fs.Seek( seekPos, seekOrigin );

				Console.WriteLine( "Seeked position " + fs.Position );

				serial.Write( "OKAY" );
				serial.Write( BitConverter.GetBytes( fs.Position ), 0, 4 );

			} catch ( System.Exception e ){
				Console.WriteLine( $"Exception when seeking file {handle}, e={e}" );
				serial.Write( "NOPE" );
				return false;
			}

		} //PCSEEK_107


		return true;

	}

}


