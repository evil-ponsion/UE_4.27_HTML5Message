// Copyright 1998-2019 Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Tools.DotNETCommon;

namespace UnrealBuildTool
{
	class HTML5ToolChain : UEToolChain
	{
		// ini configurations
		static bool enableMultithreading = false;
		static bool bMultithreading_UseOffscreenCanvas = false; // else use Offscreen_Framebuffer
		static bool enableTracing = false; // Debug option
		static string profilerMode = "none";
		static string SessionStorageCommandLineKey;

		// calculated/constructed configurations
		public static bool enableSIMD { get { return false; } } // TODO: finish Apex and NvCloth libs (compiled with SIMD support)

		public static string libExt { get { return ".a"; } }
		public static string objExt { get { return ".o"; } }
		public static string h5LibPath { get { return "lib-" + HTML5SDKInfo.EmscriptenVersion() + "-up" + (enableMultithreading ? "-mt" : ""); } }
		static bool useLLVMwasmBackend = true; // for feedback only...

		static HTML5ToolChain()
		{
			// we need to know early whether we are multithreading or not
			// so that the h5LibPath static getter will return the correct path
			// TODO: need a better/proper way of doing this (i.e. this way won't ever consider the InProjectFile available in normal constructor)
			string EngineIniPath = UnrealBuildTool.GetRemoteIniPath();
			if (!String.IsNullOrEmpty(EngineIniPath))
			{
				DirectoryReference ProjectDir = new DirectoryReference(UnrealBuildTool.GetRemoteIniPath());
				ConfigHierarchy Ini = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine, ProjectDir, UnrealTargetPlatform.HTML5);
				
				Ini.GetBool("/Script/HTML5PlatformEditor.HTML5TargetSettings", "EnableMultithreading", out enableMultithreading);

				Log.TraceInformation("h5conf HTML5ToolChain (static constructor): EnableMultithreading = " + enableMultithreading);
			}
		}
		
		// verbose feedback
		delegate void VerbosePrint(CppConfiguration Configuration, bool bOptimizeForSize);	// proto
		static VerbosePrint PrintOnce = new VerbosePrint(PrintOnceOn);						// fn ptr
		static void PrintOnceOff(CppConfiguration Configuration, bool bOptimizeForSize) {}	// noop
		static void PrintOnceOn(CppConfiguration Configuration, bool bOptimizeForSize)
		{
			if (Configuration == CppConfiguration.Debug)
				Log.TraceInformation("h5conf HTML5ToolChain: " + Configuration + " -O0 faster compile time");
			else if (bOptimizeForSize)
				Log.TraceInformation("h5conf HTML5ToolChain: " + Configuration + " -Oz favor size over speed");
			else if (Configuration == CppConfiguration.Development)
				Log.TraceInformation("h5conf HTML5ToolChain: " + Configuration + " -O1 fast compile time");
				//Log.TraceInformation("h5conf HTML5ToolChain: " + Configuration + " -O2 aggressive size and speed optimization");
			else if (Configuration == CppConfiguration.Shipping)
				Log.TraceInformation("h5conf HTML5ToolChain: " + Configuration + " -O3 favor speed over size");
			PrintOnce = new VerbosePrint(PrintOnceOff); // clear
		}

		public HTML5ToolChain(FileReference InProjectFile)
		{
			if (!HTML5SDKInfo.IsSDKInstalled())
			{
				throw new BuildException("HTML5 SDK is not installed; cannot use toolchain.");
			}

			// ini configs
			// - normal ConfigCache w/ UnrealBuildTool.ProjectFile takes all game config ini files
			//   (including project config ini files)
			// - but, during packaging, if -remoteini is used -- need to use UnrealBuildTool.GetRemoteIniPath()
			//   (note: ConfigCache can take null ProjectFile)
			string EngineIniPath = UnrealBuildTool.GetRemoteIniPath();
			DirectoryReference ProjectDir = !String.IsNullOrEmpty(EngineIniPath) ? new DirectoryReference(EngineIniPath)
												: DirectoryReference.FromFile(InProjectFile);
			ConfigHierarchy Ini = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine, ProjectDir, UnrealTargetPlatform.HTML5);

			Ini.GetBool("/Script/HTML5PlatformEditor.HTML5TargetSettings", "EnableMultithreading", out enableMultithreading);
			Ini.GetBool("/Script/HTML5PlatformEditor.HTML5TargetSettings", "OffscreenCanvas", out bMultithreading_UseOffscreenCanvas);
//			Ini.GetBool("/Script/HTML5PlatformEditor.HTML5TargetSettings", "LLVMWasmBackend", out useLLVMwasmBackend);
			Ini.GetBool("/Script/HTML5PlatformEditor.HTML5TargetSettings", "EnableTracing", out enableTracing);
			Ini.GetString("/Script/HTML5PlatformEditor.HTML5TargetSettings", "EmscriptenProfilerMode", out profilerMode);
			Ini.GetString("/Script/HTML5PlatformEditor.HTML5TargetSettings", "SessionStorageCommandLineKey", out SessionStorageCommandLineKey);

			Log.TraceInformation("h5conf HTML5ToolChain: EnableMultithreading = " + enableMultithreading );
			Log.TraceInformation("h5conf HTML5ToolChain: OffscreenCanvas = "      + bMultithreading_UseOffscreenCanvas );
			Log.TraceInformation("h5conf HTML5ToolChain: LLVMWasmBackend = "      + useLLVMwasmBackend   );
			Log.TraceInformation("h5conf HTML5ToolChain: EnableTracing = "        + enableTracing        );
			Log.TraceInformation("h5conf HTML5ToolChain: ProfilerMode = "         + profilerMode         );
			Log.TraceInformation("h5conf HTML5ToolChain: SessionStorageCommandLineKey = " + SessionStorageCommandLineKey);

			Log.TraceInformation("h5conf HTML5ToolChain: EnableSIMD = "           + enableSIMD           );
			Log.TraceInformation("h5conf HTML5ToolChain: 3rd party lib path = "   + h5LibPath            );

			PrintOnce = new VerbosePrint(PrintOnceOn); // reset

			Log.TraceInformation("h5conf Setting Emscripten SDK: located in " + HTML5SDKInfo.EMSCRIPTEN_ROOT);
			string TempDir = HTML5SDKInfo.SetupEmscriptenTemp();

			if (Environment.GetEnvironmentVariable("EMSDK") == null) // If EMSDK is present, Emscripten is already configured by the developer
			{
				// use emsdk's generated .emscripten
				Environment.SetEnvironmentVariable("EM_CONFIG", HTML5SDKInfo.DOT_EMSCRIPTEN);
				Environment.SetEnvironmentVariable("EM_CACHE", HTML5SDKInfo.EMSCRIPTEN_CACHE);
				Environment.SetEnvironmentVariable("EMCC_TEMP_DIR", TempDir);
			}

			Log.TraceInformation("h5conf Emscripten Config File: " + Environment.GetEnvironmentVariable("EM_CONFIG"));
		}

		string GetSharedArguments_Global(CppConfiguration Configuration, bool bOptimizeForSize, string Architecture, WarningLevel ShadowVariableWarningLevel, bool bEnableUndefinedIdentifierWarnings, bool bUndefinedIdentifierWarningsAsErrors, bool bUseInlining)
		{
			string Result = " ";
//			string Result = " -Werror";

			Result += " -fdiagnostics-format=msvc";
			Result += " -fno-exceptions";
//			Result += " -s DISABLE_EXCEPTION_CATCHING=1"; // as of 1.38.45, need this switch when using -fno-exceptions to skip error check and force loading library_exceptions.js (EPIC EDIT)

			Result += " -Wdelete-non-virtual-dtor";
			Result += " -Wno-switch"; // many unhandled cases
			Result += " -Wno-tautological-constant-out-of-range-compare"; // comparisons from TCHAR being a char
			Result += " -Wno-tautological-compare"; // comparison of unsigned expression < 0 is always false" (constant comparisons, which are possible with template arguments)
			Result += " -Wno-tautological-undefined-compare"; // pointer cannot be null in well-defined C++ code; comparison may be assumed to always evaluate
			Result += " -Wno-inconsistent-missing-override"; // as of 1.35.0, overriding a member function but not marked as 'override' triggers warnings
			Result += " -Wno-undefined-var-template"; // 1.36.11
			Result += " -Wno-invalid-offsetof"; // using offsetof on non-POD types
			Result += " -Wno-gnu-string-literal-operator-template"; // allow static FNames
			Result += " -Wno-final-dtor-non-final-class"; // as of 1.39.0 upstream TGraphTask warning
			Result += " -Wno-implicit-int-float-conversion"; // as of 1.39.0 upstream
			Result += " -Wno-single-bit-bitfield-constant-conversion";
			Result += " -Wno-invalid-unevaluated-string";


			Result += " -Wno-deprecated-builtins";
			// TODO: add clang pragmas around the lines relating to templates causing the warnings in the engine so we can re-enable shadow warnings
			Result += " -Wno-shadow";

			// as of emscripten 3.1.69 due to StringView.h _SV etc.
			Result += " -Wno-deprecated-literal-operator";

			// as of emscripten 3.1.73 due to warnings on memcpy/memset
			Result += " -Wno-nontrivial-memaccess";

			//Result += " -Wno-nonnull";

			//if (ShadowVariableWarningLevel != WarningLevel.Off)
			//{
			//	Result += " -Wshadow" ;//+ (bShadowVariableWarningsAsErrors ? "" : " -Wno-error=shadow");
			//}

			if (bEnableUndefinedIdentifierWarnings)
			{
				Result += " -Wundef" ;//+ (bUndefinedIdentifierWarningsAsErrors ? "" : " -Wno-error=undef");
			}

			// --------------------------------------------------------------------------------

			if (Configuration == CppConfiguration.Debug)
			{															// WARNING: UEBuildTarget.cs :: GetCppConfiguration()
				Result += " -O0"; // faster compile time				//          DebugGame is forced to Development
			}															// i.e. this will never get hit...

			else if (bOptimizeForSize)
			{															// Engine/Source/Programs/UnrealBuildTool/HTML5/UEBuildHTML5.cs
				Result += " -Oz"; // favor size over speed				// bCompileForSize=true; // set false, to build -O2 or -O3
			}															// SOURCE BUILD ONLY

			else if (Configuration == CppConfiguration.Development)
			{
				Result += " -O1"; // fast compile time
				//Result += " -O2"; // aggressive size and speed optimization
			}

			else if (Configuration == CppConfiguration.Shipping)
			{
				Result += " -O3"; // favor speed over size
			}

			if (!bUseInlining)
			{
				Result += " -fno-inline-functions";
			}

			PrintOnce(Configuration, bOptimizeForSize);

			// --------------------------------------------------------------------------------

			// JavaScript option overrides (see src/settings.js)
			if (enableSIMD)
			{
//				Result += " -msse2 -s SIMD=1";
				Result += " -s SIMD=1";
			}

			if (enableMultithreading)
			{
//				Result += " -msse2 -s USE_PTHREADS=1";
				Result += " -s USE_PTHREADS=1";
				Result += " -DEXPERIMENTAL_OPENGL_RHITHREAD=" + (bMultithreading_UseOffscreenCanvas ? "0" : "1");

				// NOTE: use "emscripten native" video, keyboard, mouse
			}
			else
			{
				// SDL2 is not supported for multi-threading WASM builds
				// WARNING: SDL2 may be removed in a future UE4 release
				// can comment out to use "emscripten native" single threaded
	//			Result += " -DHTML5_USE_SDL2";
			}

			//Result += " -s WASM_OBJECT_FILES=1";
			Environment.SetEnvironmentVariable("EMCC_WASM_BACKEND", "1");

			// --------------------------------------------------------------------------------

			// emscripten ports
// WARNING: seems emscripten ports cannot be currently used
// there might be UE4 changes needed that are found in Engine/Source/ThirdParty/...
// and built from Engine/Platforms/HTML5/Build/BatchFiles/Build_All_HTML5_libs.sh -- all via the CMake system

//			Result += " -s USE_ZLIB=1";
//			Result += " -s USE_LIBPNG=1";
//			Result += " -s USE_VORBIS=1";
//			Result += " -s USE_OGG=1";
//			Result += " -s USE_FREETYPE=1";
//			Result += " -s USE_HARFBUZZ=1";
//			Result += " -s USE_ICU=1";

			// SDL_Audio needs to be linked in [no matter if -DHTML5_USE_SDL2 is used or not]
// TODO: remove AudioMixerSDL from Engine/Source/Runtime/Launch/Launch.Build.cs and replace with emscripten native functions
//			Result += " -s USE_SDL=2";

			// --------------------------------------------------------------------------------

			// Expect that Emscripten SDK has been properly set up ahead in time (with emsdk and prebundled toolchains this is always the case)
			// This speeds up builds a tiny bit.
			Environment.SetEnvironmentVariable("EMCC_SKIP_SANITY_CHECK", "1");

			// THESE ARE TEST/DEBUGGING -- TRY NOT TO USE THESE
//			Environment.SetEnvironmentVariable("EMCC_DEBUG", "1"); // NOTE: try to use -v instead of EMCC_DEBUG
//			Environment.SetEnvironmentVariable("EMCC_DEBUG_SAVE", "1"); // very useful for compiler bughunts
//			Environment.SetEnvironmentVariable("EMCC_CORES", "8");
//			Environment.SetEnvironmentVariable("EMCC_OPTIMIZE_NORMALLY", "1");

			// enable verbose mode
//			Result += " -v"; // useful for path hunting issues

			if (BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Linux)
			{
				// Packaging on Linux needs this - or else system clang will be attempted to be picked up instead of UE4's included emsdk
				Environment.SetEnvironmentVariable(HTML5SDKInfo.PLATFORM_USER_HOME, HTML5SDKInfo.HTML5Intermediatory);
			}
			if (BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Win64 || BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Win32)
			{
				// Packaging on Window needs this - zap any existing HOME environment variables to prevent any accidental pick ups
				Environment.SetEnvironmentVariable("HOME", "");
			}
			
			// may need to force allow command line arguments to shipping build to allow command line from session storage
			if (Configuration == CppConfiguration.Shipping && !string.IsNullOrEmpty(SessionStorageCommandLineKey)) {
				// Log.TraceInformation("h5conf HTML5ToolChain: Forcing UE_ALLOW_MAP_OVERRIDE_IN_SHIPPING=1 to support Session Storage Command Line Key");
				Result += " -DUE_ALLOW_MAP_OVERRIDE_IN_SHIPPING=1";
			}
			
			return Result;
		}

		string GetCLArguments_Global(CppCompileEnvironment CompileEnvironment)
		{
			string Result = GetSharedArguments_Global(CompileEnvironment.Configuration, CompileEnvironment.bOptimizeForSize, CompileEnvironment.Architecture, CompileEnvironment.ShadowVariableWarningLevel, CompileEnvironment.bEnableUndefinedIdentifierWarnings, CompileEnvironment.bUndefinedIdentifierWarningsAsErrors, CompileEnvironment.bUseInlining);

			return Result;
		}

		static string GetCLArguments_CPP(CppCompileEnvironment CompileEnvironment)
		{
			var Mapping = new Dictionary<CppStandardVersion, string>
			{
				{ CppStandardVersion.Cpp14, " -std=c++14" },
				{ CppStandardVersion.Cpp17, " -std=c++17" },
				{ CppStandardVersion.Latest, " -std=c++17" },
				{ CppStandardVersion.Default, " -std=c++14" }
			};
			return Mapping[CompileEnvironment.CppStandard];
		}

		static string GetCLArguments_C(string Architecture)
		{
			string Result = "";

			return Result;
		}

		string GetLinkArguments(LinkEnvironment LinkEnvironment)
		{
			string Result = GetSharedArguments_Global(LinkEnvironment.Configuration, LinkEnvironment.bOptimizeForSize, LinkEnvironment.Architecture, WarningLevel.Off, false, false, false);

			/* N.B. When editing link flags in this function, UnrealBuildTool does not seem to automatically pick them up and do an incremental
			 *	relink only of UE4Game.js (at least when building blueprints projects). Therefore after editing, delete the old build
			 *	outputs to force UE4 to relink:
			 *
			 *    > rm Engine/Binaries/HTML5/UE4Game.bc
			 */


			// --------------------------------------------------
			// do we want debug info?

			if (LinkEnvironment.Configuration == CppConfiguration.Debug || LinkEnvironment.bCreateDebugInfo)
			{
				// As a lightweight alternative, just retain function names in output.
				Result += " --profiling-funcs";

				// dump headers: http://stackoverflow.com/questions/42308/tool-to-track-include-dependencies
//				Result += " -H";
			}
			else if (LinkEnvironment.Configuration == CppConfiguration.Development)
			{
				// Development builds always have their function names intact.
				Result += " --profiling-funcs";
			}

			// Emit a .symbols map file of the minified function names. (on -g2 builds this has no effect)
			Result += " --emit-symbol-map";

//			if (LinkEnvironment.Configuration != CppConfiguration.Debug)
//			{
//				if (LinkEnvironment.bOptimizeForSize) Result += " -s OUTLINING_LIMIT=40000";
//				else Result += " -s OUTLINING_LIMIT=110000";
//			}

			if (LinkEnvironment.Configuration == CppConfiguration.Debug || LinkEnvironment.Configuration == CppConfiguration.Development)
			{
				// check for alignment/etc checking
				//Result += " -s SAFE_HEAP=1";
				//Result += " -s SAFE_HEAP_LOG=1";
				//Result += " -s CHECK_HEAP_ALIGN=1";
				//Result += " -s SAFE_DYNCALLS=1";

				// enable assertions in non-Shipping/Release builds
				Result += " -s ASSERTIONS=1";
				Result += " -s GL_ASSERTIONS=1";
				//Result += " -s ASSERTIONS=2";
				//Result += " -s GL_ASSERTIONS=2";
				//Result += " -s GL_DEBUG=1";
				//Result += " -s PTHREADS_DEBUG=1";
				//Result += " -s SOCKET_DEBUG=1";
				
				//Result += " -s STACK_OVERFLOW_CHECK=2";

				// In non-shipping builds, don't run ctol evaller, it can take a bit of extra time.
				// Result += " -s EVAL_CTORS=0";

				// --------------------------------------------------

				// UE4Game.js.symbols is redundant if -g2 is passed (i.e. --emit-symbol-map gets ignored)
				Result += " -g1";

//				// add source map loading to code
//				string source_map = Path.Combine(HTML5SDKInfo.EMSCRIPTEN_ROOT, "src", "emscripten-source-map.min.js");
//				source_map = source_map.Replace("\\", "/").Replace(" ","\\ "); // use "unix path" and escape spaces
//				Result += " --pre-js " + source_map;

				// link in libcxxabi demang
				//Result += " -s DEMANGLE_SUPPORT=1"; // commented out due to warning with emscripten 3.1.61 about this being deprecated

				// --------------------------------------------------

				if ( profilerMode.Equals("cpu", StringComparison.InvariantCultureIgnoreCase))
				{
					Result += " --cpuprofiler";
				}
				else if ( profilerMode.Equals("memory", StringComparison.InvariantCultureIgnoreCase))
				{
					Result += " --memoryprofiler";
				}
				else if ( profilerMode.Equals("thread", StringComparison.InvariantCultureIgnoreCase))
				{
					if (enableMultithreading)
					{
						Result += " --threadprofiler";
					}
				}
				// else none
			}

			if (enableTracing)
			{
				Result += " --tracing";
			}


			// --------------------------------------------------
			// emscripten memory

			if (enableMultithreading)
			{
				Result += " -s ALLOW_MEMORY_GROWTH=0";
				Result += " -s INITIAL_MEMORY=600MB";
// NOTE: browsers needs to temporarly have some flags set:
//  https://github.com/kripken/emscripten/wiki/Pthreads-with-WebAssembly
//  https://kripken.github.io/emscripten-site/docs/porting/pthreads.html
				Result += " -s PTHREAD_POOL_SIZE=4";// -s PTHREAD_HINT_NUM_CORES=2";
			}
			else
			{
				Result += " -s ALLOW_MEMORY_GROWTH=1";
				Result += " -s INITIAL_MEMORY=32MB";
			}

			// emscripten 3.1.27 has a smaller default stack size
			// so set back to the old default of 5MB to avoid stack overflow
			Result += " -s STACK_SIZE=5MB";

			// --------------------------------------------------
			// WebGL

			// NOTE: UE-51094 UE-51267 -- always USE_WEBGL2, webgl1 only feature can be switched on the fly via url paramater "?webgl1"
//			if (targetWebGL2)
			{
				Result += " -s USE_WEBGL2=1";
				if ( enableMultithreading )
				{
					if ( bMultithreading_UseOffscreenCanvas )
					{
						Result += " -s OFFSCREENCANVAS_SUPPORT=1";
					}
					else
					{
						Result += " -s OFFSCREEN_FRAMEBUFFER=1";
					}
					Result += " -s PROXY_TO_PTHREAD=1";
				}

				// Also enable WebGL 1 emulation in WebGL 2 contexts. This adds backwards compatibility related features to WebGL 2,
				// such as:
				//  - keep supporting GL_EXT_shader_texture_lod extension in GLSLES 1.00 shaders
				//  - support for WebGL1 unsized internal texture formats
				//  - mask the GL_HALF_FLOAT_OES != GL_HALF_FLOAT mixup
//				Result += " -s WEBGL2_BACKWARDS_COMPATIBILITY_EMULATION=1";
//				Result += " -s FULL_ES3=1";
				Result += " -s MIN_WEBGL_VERSION=2";
				Result += " -s MAX_WEBGL_VERSION=2";
			}
//			else
//			{
//				Result += " -s FULL_ES2=1";
//			}

			// The HTML page template precreates the WebGL context, so instruct the runtime to hook into that if available.
			Result += " -s GL_PREINITIALIZED_CONTEXT=1";

			//Result += " -s DISABLE_DEPRECATED_FIND_EVENT_TARGET_BEHAVIOR=0";

			// --------------------------------------------------
			// wasm

			//Result += " -s WASM=1";


			// --------------------------------------------------
			// house keeping

			// export console command handler. Export main func too because default exports ( e.g Main ) are overridden if we use custom exported functions.
			Result += " -s EXPORTED_FUNCTIONS=\"['_main', '_on_fatal', '_emscripten_webgl_get_current_context', '_emscripten_webgl_make_context_current', '_htons', '_ntohs', '_malloc','_free','_sendue']\""; // , '__get_daylight', '__get_timezone', '__get_tzname'
//XXXResult += " -s EXPORTED_FUNCTIONS=\"['_main', '_on_fatal', '__cxa_uncaught_exceptions' ,'__cxa_allocate_exception']\""; // BUGHUNT: broken compiler
			Result += " -s EXTRA_EXPORTED_RUNTIME_METHODS=\"['Pointer_stringify', 'writeAsciiToMemory', 'stackTrace','ccall','cwrap']\"";
			Result += " -s EXPORTED_RUNTIME_METHODS=\"['stringToAscii']\"";

			Result += " -s ERROR_ON_UNDEFINED_SYMBOLS=1";
//XXXResult += " -s ERROR_ON_UNDEFINED_SYMBOLS=0"; // BUGHUNT: broken compiler
			Result += " -s NO_EXIT_RUNTIME=1";

			Result += " -s LLD_REPORT_UNDEFINED";
			// Result += " -error-limit=0";

			// --------------------------------------------------
			// emscripten filesystem

			Result += " -s CASE_INSENSITIVE_FS=1";
			Result += " -s FORCE_FILESYSTEM=1";

//			if (enableMultithreading)
//			{
// was recommended to NOT use either of these...
//				Result += " -s ASYNCIFY=1"; // alllow BLOCKING calls (i.e. sleep)
//				Result += " -s EMTERPRETIFY_ASYNC=1"; // alllow BLOCKING calls (i.e. sleep)
//			}

			// TODO: ASMFS


			// --------------------------------------------------
//if (useLLVMwasmBackend && !enableMultithreading) { Result += " --no-check-features -error-limit=0"; } // BUGHUNT: broken compiler

			//Log.TraceInformation("LinkArguments: " + Result);
			
			return Result;
		}

		static string GetLibArguments(LinkEnvironment LinkEnvironment)
		{
			string Result = "";

			return Result;
		}

		public void AddIncludePath(ref string Arguments, DirectoryReference IncludePath)
		{
			if(IncludePath.IsUnderDirectory(UnrealBuildTool.EngineDirectory))
			{
				Arguments += string.Format(" -I\"{0}\"", IncludePath.MakeRelativeTo(UnrealBuildTool.EngineSourceDirectory));
			}
			else
			{
				Arguments += string.Format(" -I\"{0}\"", IncludePath);
			}
		}

		public override CPPOutput CompileCPPFiles(CppCompileEnvironment CompileEnvironment, List<FileItem> InputFiles, DirectoryReference OutputDir, string ModuleName, IActionGraphBuilder Graph)
		{
			string Arguments = GetCLArguments_Global(CompileEnvironment);

			CPPOutput Result = new CPPOutput();

			// Add include paths to the argument list.
			foreach (DirectoryReference IncludePath in CompileEnvironment.UserIncludePaths)
			{
				AddIncludePath(ref Arguments, IncludePath);
			}
			foreach (DirectoryReference IncludePath in CompileEnvironment.SystemIncludePaths)
			{
				AddIncludePath(ref Arguments, IncludePath);
			}


			// Add preprocessor definitions to the argument list.
			foreach (string Definition in CompileEnvironment.Definitions)
			{
				Arguments += string.Format(" -D{0}", Definition);
			}

			if (enableTracing)
			{
				Arguments += string.Format(" -D__EMSCRIPTEN_TRACING__");
			}

			// Force include all the requested headers
			foreach(FileItem ForceIncludeFile in CompileEnvironment.ForceIncludeFiles)
			{
				Arguments += String.Format(" -include \"{0}\"", ForceIncludeFile.Location);
			}

			foreach (FileItem SourceFile in InputFiles)
			{
				Action CompileAction = Graph.CreateAction(ActionType.Compile);
				CompileAction.CommandDescription = "Compile";
				CompileAction.PrerequisiteItems.AddRange(CompileEnvironment.ForceIncludeFiles);
//				CompileAction.bPrintDebugInfo = true;

				bool bIsPlainCFile = Path.GetExtension(SourceFile.AbsolutePath).ToUpperInvariant() == ".C";

				// Add the C++ source file and its included files to the prerequisite item list.
				CompileAction.PrerequisiteItems.Add(SourceFile);

				// Add the source file path to the command-line.
				string FileArguments = string.Format(" -c \"{0}\"", SourceFile.AbsolutePath);

				// Add the object file to the produced item list.
				FileItem ObjectFile = FileItem.GetItemByFileReference(FileReference.Combine(OutputDir, Path.GetFileName(SourceFile.AbsolutePath) + objExt));
				CompileAction.ProducedItems.Add(ObjectFile);
				FileArguments += string.Format(" -o \"{0}\"", ObjectFile.AbsolutePath);

				// Add C or C++ specific compiler arguments.
				if (bIsPlainCFile)
				{
					FileArguments += GetCLArguments_C(CompileEnvironment.Architecture);
				}
				else
				{
					FileArguments += GetCLArguments_CPP(CompileEnvironment);
				}

				// Generate the included header dependency list
				if(CompileEnvironment.bGenerateDependenciesFile)
				{
					FileItem DependencyListFile = FileItem.GetItemByFileReference(FileReference.Combine(OutputDir, Path.GetFileName(SourceFile.AbsolutePath) + ".d"));
					FileArguments += string.Format(" -MD -MF\"{0}\"", DependencyListFile.AbsolutePath.Replace('\\', '/'));
					CompileAction.DependencyListFile = DependencyListFile;
					CompileAction.ProducedItems.Add(DependencyListFile);
				}

				CompileAction.WorkingDirectory = UnrealBuildTool.EngineSourceDirectory;
				CompileAction.CommandPath = HTML5SDKInfo.Python();

				CompileAction.CommandArguments = HTML5SDKInfo.EmscriptenCompiler() + " " + Arguments + FileArguments + CompileEnvironment.AdditionalArguments;

				//System.Console.WriteLine(CompileAction.CommandArguments);
				CompileAction.StatusDescription = Path.GetFileName(SourceFile.AbsolutePath);

				// Don't farm out creation of precomputed headers as it is the critical path task.
				CompileAction.bCanExecuteRemotely = CompileEnvironment.PrecompiledHeaderAction != PrecompiledHeaderAction.Create;

				// this is the final output of the compile step (a .abc file)
				Result.ObjectFiles.Add(ObjectFile);

				// VC++ always outputs the source file name being compiled, so we don't need to emit this ourselves
				CompileAction.bShouldOutputStatusDescription = true;

				// Don't farm out creation of precompiled headers as it is the critical path task.
				CompileAction.bCanExecuteRemotely =
					CompileEnvironment.PrecompiledHeaderAction != PrecompiledHeaderAction.Create ||
					CompileEnvironment.bAllowRemotelyCompiledPCHs;
			}

			return Result;
		}

		public override FileItem LinkFiles(LinkEnvironment LinkEnvironment, bool bBuildImportLibraryOnly, IActionGraphBuilder Graph)
		{
			FileItem OutputFile;

			// Make the final javascript file
			Action LinkAction = Graph.CreateAction(ActionType.Link);
			LinkAction.CommandDescription = "Link";
//			LinkAction.bPrintDebugInfo = true;

			// ResponseFile lines.
			List<string> ReponseLines = new List<string>();

			LinkAction.bCanExecuteRemotely = false;
			LinkAction.WorkingDirectory = UnrealBuildTool.EngineSourceDirectory;
			LinkAction.CommandPath = HTML5SDKInfo.Python();
			LinkAction.CommandArguments = HTML5SDKInfo.EmscriptenCompiler();
//			bool bIsBuildingLibrary = LinkEnvironment.bIsBuildingLibrary || bBuildImportLibraryOnly;
//			ReponseLines.Add(
//					bIsBuildingLibrary ?
//					GetLibArguments(LinkEnvironment) :
//					GetLinkArguments(LinkEnvironment)
//				);
			ReponseLines.Add(GetLinkArguments(LinkEnvironment));

			// Add the input files to a response file, and pass the response file on the command-line.
			foreach (FileItem InputFile in LinkEnvironment.InputFiles)
			{
				//System.Console.WriteLine("File  {0} ", InputFile.AbsolutePath);
				ReponseLines.Add(string.Format(" \"{0}\"", InputFile.AbsolutePath));
				LinkAction.PrerequisiteItems.Add(InputFile);
			}

			if (!LinkEnvironment.bIsBuildingLibrary)
			{
				// Make sure ThirdParty libs are at the end.
				List<FileReference> ThirdParty = (from Lib in LinkEnvironment.Libraries
											where Lib.FullName.Contains("ThirdParty")
											select Lib).ToList();

				LinkEnvironment.Libraries.RemoveAll(Element => Element.FullName.Contains("ThirdParty"));
				LinkEnvironment.Libraries.AddRange(ThirdParty);

				foreach (FileReference Library in LinkEnvironment.Libraries)
				{
					FileItem Item = FileItem.GetItemByPath(Library.FullName);

					if (Item.AbsolutePath.Contains(".lib"))
						continue;

					if (Item.ToString().EndsWith(".js"))
						ReponseLines.Add(string.Format(" --js-library \"{0}\"", Item.AbsolutePath));


					// WARNING: With --pre-js and --post-js, the order in which these directives are passed to
					// the compiler is very critical, because that dictates the order in which they are appended.
					//
					// Set environment variable [ EMCC_DEBUG=1 ] to see the linker order used in packaging.
					//     See GetSharedArguments_Global() above to set this environment variable

					else if (Item.ToString().EndsWith(".jspre"))
						ReponseLines.Add(string.Format(" --pre-js \"{0}\"", Item.AbsolutePath));

					else if (Item.ToString().EndsWith(".jspost"))
						ReponseLines.Add(string.Format(" --post-js \"{0}\"", Item.AbsolutePath));


					else
						ReponseLines.Add(string.Format(" \"{0}\"", Item.AbsolutePath));

					LinkAction.PrerequisiteItems.Add(Item);
				}
			}
			// make the file we will create


			OutputFile = FileItem.GetItemByFileReference(LinkEnvironment.OutputFilePath);
			LinkAction.ProducedItems.Add(OutputFile);
			ReponseLines.Add(string.Format(" -o \"{0}\"", OutputFile.AbsolutePath));
Log.TraceInformation("XXX: -o " + OutputFile.AbsolutePath);

//#if XXX_USE_FASTCOMP
//			ReponseLines.Add(string.Format(" --save-bc \"{0}\"", OutputLink.AbsolutePath));
//Log.TraceInformation("XXX: --save-bc " + OutputLink.AbsolutePath);
//#endif
			FileItem OutputLink = FileItem.GetItemByPath(LinkEnvironment.OutputFilePath.FullName.Replace(".js", ".wasm"));
			LinkAction.ProducedItems.Add(OutputLink);

			LinkAction.StatusDescription = Path.GetFileName(OutputFile.AbsolutePath);

			FileReference ResponseFileName = GetResponseFileName(LinkEnvironment, OutputFile);

			// this is needed when using EMCC_DEBUG_SAVE
			if (!FileReference.Exists(ResponseFileName))
			{
				DirectoryReference.CreateDirectory(ResponseFileName.Directory);
			}
			FileItem ResponseFileItem = Graph.CreateIntermediateTextFile(ResponseFileName, ReponseLines);

			LinkAction.CommandArguments += string.Format(" @\"{0}\"", ResponseFileName);
			LinkAction.PrerequisiteItems.Add(ResponseFileItem);

			return OutputFile;
		}

		public override void ModifyBuildProducts(ReadOnlyTargetRules Target, UEBuildBinary Binary, List<string> Libraries, List<UEBuildBundleResource> BundleResources, Dictionary<FileReference, BuildProductType> BuildProducts)
		{
			// we need to include the generated .wasm and .symbols file.
			if (Binary.Type != UEBuildBinaryType.StaticLibrary)
			{
				BuildProducts.Add(Binary.OutputFilePath.ChangeExtension("wasm"), BuildProductType.RequiredResource);
				BuildProducts.Add(Binary.OutputFilePath + ".symbols", BuildProductType.RequiredResource);
			}
		}
	};
}
