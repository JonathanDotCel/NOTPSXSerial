
![](social_card_PNG.png)

# NoPS - NotPsxSerial
Aug 2022 - github.com/JonathanDotCel

Serial transfer suite for Playstation 1 / Unirom

# Links

Discord: https://psx.dev
Twitter: https://twitter.com/jonathandotcel

More detailed instructions: https:\\unirom.github.io
(Including GDB, debugging, etc)


# Features

    * Kernel-resident debugging
    Debug running games on a retail console

    * Cart/EEPROM flasher
    Back up and reflash cheat carts

    * .EXE or raw binary upload
    Run homebrew utilities or upload assets for development
    
    * Memorycard tools
    Backup or rewrite whole memory cards to raw (emulator) format
    
    * Peeks n Pokes
    Peek or poke running games/homebrew (incl cheat codes)
   
    * Mem dumps
    Grab a copy of RAM, cart EEPROM, BIOS, etc
    
    * TCP->SIO Bridge
    Raw communication via TCP

    * In progress: GDB TCP->SIO bridge!
    Step through your code in the editor, etc

## Upgrading to UniROM:
   
   You can use nops to upgrade a cart already running Unirom 8 or higher.
   Instructions in the unirom download/github: http://github.com/JonathanDotCel

## Supported configs:

### Cables:

  * Basic 3 wire TX/RX/GND
   (e.g. a 3 quid dongle off amazon and a couple of wires)   
  * Sharklink with handshake
  * Yaroze cables 
  * Switchable cables
  

### OS:
  * Windows via .NET or Mono
  * OSX / Linux via Mono


## Basic Usage

The hello world test executable was written by Shadow of PSXDev.net.
To run it:
* `nops /exe psx_helloworld.exe COM8`

Where COMPORT might be COM3, COM14, etc under windows and /dev/ttyWhatever under *nix with mono.
win: Find it by opening `devmgmt.msc` from the run box.
mac + linux: run ./nops for some example device names

e.g.
* `nops /rom unirom_s.rom COM14`
* `./nops /rom unirom_s.rom /dev/ttyUSB01`

Once you've specified a com port in this directory, it will be saved to `COMPORT.TXT`

so next time around you just need to:

* `nops /rom unirom_s.rom`

### Uploading Stuff

Your basic "Upload to the PSX" features are:

* `nops /exe whatever.exe`
* `nops /rom whatever.rom`
* `nops /bin 0x8000ADDR somefile.whatever`

Where /exe executs a file from the header information, /rom will attempt to identify the JEDEC chip and flash it, and /bin will just upload a binary file to a specific address.

### Flow Control

To jump to an address
* `nops /jump 0x80?????? `

To call an address (and return)
* `nops /call 0x80?????? `

To poke an 8, 16 or 32 bit value
* `nops /poke8 0x80100000 0x01`
* `nops /poke16 0x801F0012 0xFFEE`
* `nops /poke32 0xA000C000 0x00112233`

### Reading data

 And to get the information back:
* ` nops /dump 0x80???? 0xSIZE`
 
 Or watch it oncreen:
*  `nops /watch 0x80030000 0xSIZE`

### Misc

returns PONG!
* `nops /ping `

Restart the machine
* `nops /reset`


### Other operators:

`/m`
Use in conjunction with anything else to keep the serial connection open and view printf/TTY logs:
E.g.
* `nops /exe whatever.exe /m`
* `nops /rom whatever.rom /m`

or simply
`nops /m`
if you just want to listen.

### /fast and /slow

Every command also supports `/fast`
This will send a low speed ping to the PSX tell it to bump from 115200 to 518400

Note:
Once in `/fast` mode, the next time you run nops, it has no way to tell
So remember to include `/fast` in every command till you restart!


# Debug operators:

### This is just the list - Examples below.

The debug functions let you use nops while a game or homebrew is running.
It copies a small SIO/Debug handler into unused kernel memory and remains resident through gameplay.

Enter debug mode one of these ways:
* `L1+Square`
* `nops /debug`

## The "Halt State"

There's 3 ways to enter the halt state and they all behave the same way.

* You did `nops /halt`
* The game crashed
* A breakpoint/hook triggered

You can continue to talk to the playstation.
To exit: `nops /cont`


### Note: 
You won't see the exception handler if the game crashes in debug mode.
You'll be notified over serial though, so have `/m` ready to catch it!
`nops /cont` returns from this.
If the gods are on your side, it even recovers from some crashes.

#### Registers

This shows the registers as they were the instant the machine halted.
* `nops /regs `

Example: changing a register's value

* `nops /setreg v0 0x1F801800`
* `nops /setreg pc 0xBFC00000` *

* this one for example will restart the system when you `nops /cont`

### Note:
`/setreg` will only really work in the halt state!

### Hooks

Enter the halt state on read, write, or execution

* `nops /hookread 0xADDR`
* `nops /hookwrite 0xADDR`
* `nops /hookex 0xADDR`

One hook at a time!


# Debug Command Examples

Note: GDB bridge in progress!

As you shoul already know (because you read the topic on debug commands, right?) start with:
* ` nops /debug`
* or  `L1 + Square`

An int-driven SIO handler has now been installed into the kernel, and can talk to nops once you start a game, etc.
The command set is basically the same as for regular SIO.

## Example: Breakpoint on a particular location

Yo can do these two in whatever order:
`nops /debug`
`nops /bin 0x80030000 something.bin`

Now apply the hook
`nops /hookex 0x80031234`

And execute the binary
`nops /jal 0x80030000 /m`

### Hint!
Don't forget the `/m`
This will put nops into `monitor mode`.
Monitor mode will detect when the system has halted, and offer a debug menu.

## Example: Memory breakpoint on an exe

Again, you have some flexibility here - but you'd best enter debug mode before uploading the .exe!

`nops /debug`
`nops /hookread 0x80041234`
`nops /exe myfile.exe`

or if you want to go a bit faster:

`nops /fast /debug`
`nops /fast /hookread 0x80041234`
`nops /fast /exe myfile.exe`


### Hint!
You've specified `/fast`. Next time you start nops, it has no way of knowing if the ps is in fast mode or not.
So remember to use `/fast` on every command... or not at all!
You can switch back and forth in most cases with the following (If you're sick of typing it).
* `nops /fast`
* `nops /slow`
* `Square` 

## I'm looking at the regs, now what?

To continue from a `nops /halt`` or a crash
`/nops /cont`

I need to see the regs again!
`/nops /regs`

Okay, how do I change them?
`nops /setreg v0 0x05`



# TCP Bridge

Not a serial person?
Open a TCP bridge on port 3333
*  `nops /bridge 3333`

Connect via (e.g.)
`telnet localhost 3333`

You can now type raw commands and send raw bits n bytes through the serial port.

`REST`
to reset the system

`PING`
PONG!

In Progress:

`nops /GDB`
Like `nops /bridge` but runs `nops /debug` first.


## A big thanks:
        
      While NoPS has been re-written from the ground up for Unirom 8, before that it was
      a decompiled version of Shadow's PSXSerial with the header changed to reflect that.
      (Or more specifically to deny that). In keeping with what I've just decided is tradition,
      we're going with the same motif.

    
 
