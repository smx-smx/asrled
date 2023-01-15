<?php
/**
  * DLL wrapper generator
  * (C) 2023 Stefano Moioli <smxdev4@gmail.com>
  **/
//$DUMPBIN= "C:/Program Files (x86)/Microsoft Visual Studio/2019/Community/VC/Tools/MSVC/14.29.30037/bin/Hostx64/x64/dumpbin.exe";
$DUMPBIN = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.34.31933\bin\Hostx64\x64\dumpbin.exe';

function genproxy(string $lib_path, $def, $asm){
	global $DUMPBIN;
	$NUMBER="\d+";
	$SPACES="\s+";
	$HEX="[0-9a-fA-F]+";
	$pattern = "/(${NUMBER})${SPACES}(${HEX}){$SPACES}(${HEX})${SPACES}(.*)/";

	$lib_name = pathinfo($lib_path, PATHINFO_FILENAME);

	fwrite($def, <<<EOS
	LIBRARY {$lib_name}
	EXPORTS

	EOS);

	fwrite($asm, <<<EOS
	.intel_syntax noprefix
	.text

	EOS);

	$fn_ids = [];

	$hProc = popen(
		escapeshellarg($DUMPBIN)
		. " /EXPORTS "
		. escapeshellarg($lib_path)
	, "r");

	$proxy_id = 0;
	while(!feof($hProc)){
		$line = fgets($hProc);
		if($line === false) continue;
		if(!preg_match($pattern, $line, $m)){
			//print("skip $line\n");
			continue;
		}

		list($ordinal, $hint, $rva, $name) = [
			$m[1], $m[2], $m[3], $m[4]
		];

		$proxy_fn = "proxy_{$proxy_id}";
		$fn_ids[$proxy_id] = $name;

		fwrite($def, <<<EOS
		{$name}={$proxy_fn}

		EOS);

		fwrite($asm, <<<EOS
		.globl _{$proxy_fn}
		_{$proxy_fn}:

		push eax // save eax

		// load function name
		lea eax, str_{$proxy_id}
		push eax
		lea eax, str_lib_name
		push eax
		call _get_fptr

		// eax = [saved_eax]
		// [ret] = fptr
		xchg eax, [esp]
		ret

		EOS);

		++$proxy_id;
	}
	pclose($hProc);

	fwrite($asm, <<<EOS
	.section .rodata

	EOS);

	fwrite($asm, <<<EOS
	str_lib_name: .asciiz "{$lib_name}"

	EOS);

	foreach($fn_ids as $i => $name){
		fwrite($asm, <<<EOS
		str_{$i}: .asciz "{$name}"

		EOS);
	}
}

$lib_name = $argv[1];

$sink = fopen('NUL', 'w');
genproxy($lib_name, STDOUT, STDERR);
