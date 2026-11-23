#!/bin/bash
# Builds out/MahoragaWheel.dll. Does NOT copy into the game folder.
G="D:/SteamLibrary/steamapps/common/Casualties Unknown"; M="$G/CasualtiesUnknown_Data/Managed"; C="$G/BepInEx/core"
mkdir -p out
"/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe" -nologo -noconfig -target:library -nostdlib+ "-out:out/MahoragaWheel.dll" \
 "-r:$C/BepInEx.dll" "-r:$C/0Harmony.dll" "-r:$M/Assembly-CSharp.dll" "-r:$M/UnityEngine.dll" "-r:$M/UnityEngine.CoreModule.dll" \
 "-r:$M/mscorlib.dll" "-r:$M/netstandard.dll" "-r:$M/System.dll" "-r:$M/System.Core.dll" "-r:$M/System.Runtime.dll" "-r:$M/UnityEngine.UI.dll" "-r:$M/Unity.TextMeshPro.dll" "-r:$M/UnityEngine.UIModule.dll" "-r:$M/UnityEngine.AudioModule.dll" "-r:$M/UnityEngine.TextRenderingModule.dll" "-r:$M/UnityEngine.InputLegacyModule.dll" "-r:$M/UnityEngine.PhysicsModule.dll" "-r:$M/UnityEngine.Physics2DModule.dll" *.cs
