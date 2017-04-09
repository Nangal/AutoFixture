#r @"packages/FAKE.Core/tools/FakeLib.dll"

open Fake
open Fake.Testing
open System
open System.Diagnostics;
open System.Text.RegularExpressions

let releaseFolder = "Release"
let nunitToolsFolder = "Packages/NUnit.Runners.2.6.2/tools"
let nuGetOutputFolder = "NuGetPackages"
let signKeyPath = FullName "Src/AutoFixture.snk"
let solutionsToBuild = !! "Src/AutoFixture.AllProjects.sln"

type BuildVersionInfo = { assemblyVersion:string; fileVersion:string; nugetVersion:string }
let calculateVersionFromGit buildNumber = 
    let desc = Git.CommandHelper.runSimpleGitCommand "" "describe --tags --long --match=v*"
    // Example for regular: v3.50.2-288-g64fd5c5b, for prerelease: v3.50.2-alpha1-288-g64fd5c5b
    let result = Regex.Match(desc, @"^v(?<maj>\d+)\.(?<min>\d+)\.(?<rev>\d+)(?<pre>-\w+\d*)?-(?<num>\d+)-g(?<sha>[a-z0-9]+)$", RegexOptions.IgnoreCase).Groups
    let getMatch (name:string) = result.[name].Value

    let major, minor, revision, preReleaseSuffix, commitsNum, sha = 
        getMatch "maj" |> int, getMatch "min" |> int, getMatch "rev" |> int, getMatch "pre", getMatch "num" |> int, getMatch "sha"

    
    let assemblyVersion = sprintf "%d.%d.%d.0" major minor revision
    let fileVersion = sprintf "%d.%d.%d.%d" major minor revision buildNumber
    
    // If pre-release commits suffix is absent and we are not on the tag, we speculatively increase revision version for semantic versioning.
    // Build suffix is always appended to the end if we are not on the tag. NuGet compares strings lexically, so result will be valid.
    // It appears that NuGet doesn't fully support SemVer 2.0, therefore we format commits number with 4 digits so lexical comparison will compare values correctly.
    // We use different suffixes 'pre' and 'build' because for tags without pre-release suffix we increase revision, so it's much clear that it's a pre-release of non-existing version.
    // Examples of output: v3.50.2-pre0001, v3.50.2-pre0215, v3.50.1-rc1-build0003, v3.50.1-rc3-build0035    
    let nugetVersion = match commitsNum, preReleaseSuffix with
                       | 0, _ -> sprintf "%d.%d.%d%s" major minor revision preReleaseSuffix
                       | _, "" -> sprintf "%d.%d.%d-pre%04d" major minor (revision + 1) commitsNum
                       | _ -> sprintf "%d.%d.%d%s-build%04d" major minor revision preReleaseSuffix commitsNum

    { assemblyVersion=assemblyVersion; fileVersion=fileVersion; nugetVersion=nugetVersion }

// Define global variable with version that should be used for the build. This data is required in the multiple targets, so is defined globally.
// Please never name build parameter as "Version"" - it might be consumed by the MSBuild which break some tasks (e.g. NuGet restore)
let buildVersion = match getBuildParamOrDefault "BuildVersion" "git" with
                   | "git"       -> calculateVersionFromGit (getBuildParamOrDefault "BuildNumber" "0" |> int)
                   | assemblyVer -> { assemblyVersion = assemblyVer
                                      fileVersion = getBuildParamOrDefault "BuildFileVersion" assemblyVer
                                      nugetVersion = getBuildParamOrDefault "BuildNugetVersion" assemblyVer }



Target "PatchAssemblyVersions" (fun _ ->
    printfn 
        "Patching assembly versions. Assembly version: %s, File version: %s, NuGet version: %s" 
        buildVersion.assemblyVersion 
        buildVersion.fileVersion 
        buildVersion.nugetVersion

    let filesToPatch = !! "Src/*/Properties/AssemblyInfo.*"
    ReplaceAssemblyInfoVersionsBulk filesToPatch (fun f -> { f with AssemblyVersion              = buildVersion.assemblyVersion
                                                                    AssemblyFileVersion          = buildVersion.fileVersion
                                                                    AssemblyInformationalVersion = buildVersion.nugetVersion })
)

let build target configuration =
    let properties = [ "Configuration", configuration
                       "AssemblyOriginatorKeyFile", signKeyPath
                       "AssemblyVersion", buildVersion.assemblyVersion
                       "FileVersion", buildVersion.fileVersion
                       "InformationalVersion", buildVersion.nugetVersion ]

    solutionsToBuild
    |> MSBuild "" target properties
    |> ignore

let clean   = build "Clean"
let rebuild = build "Rebuild"

Target "CleanAll"           (fun _ -> ())
Target "CleanVerify"        (fun _ -> clean "Verify")
Target "CleanRelease"       (fun _ -> clean "Release")
Target "CleanReleaseFolder" (fun _ -> CleanDir releaseFolder)

Target "Verify" (fun _ -> rebuild "Verify")

Target "BuildOnly" (fun _ -> rebuild "Release")
Target "TestOnly" (fun _ ->
    let configuration = getBuildParamOrDefault "Configuration" "Release"
    let parallelizeTests = getBuildParamOrDefault "ParallelizeTests" "False" |> Convert.ToBoolean
    let maxParallelThreads = getBuildParamOrDefault "MaxParallelThreads" "0" |> Convert.ToInt32
    let parallelMode = if parallelizeTests then ParallelMode.All else ParallelMode.NoParallelization
    let maxThreads = if maxParallelThreads = 0 then CollectionConcurrencyMode.Default else CollectionConcurrencyMode.MaxThreads(maxParallelThreads)

    let testAssemblies = !! (sprintf "Src/*Test/bin/%s/*Test.dll" configuration)
                         -- (sprintf "Src/AutoFixture.NUnit*.*Test/bin/%s/*Test.dll" configuration)

    testAssemblies
    |> xUnit2 (fun p -> { p with Parallel = parallelMode
                                 MaxThreads = maxThreads })

    let nunit2TestAssemblies = !! (sprintf "Src/AutoFixture.NUnit2.*Test/bin/%s/*Test.dll" configuration)

    nunit2TestAssemblies
    |> NUnit (fun p -> { p with StopOnError = false
                                OutputFile = "NUnit2TestResult.xml" })

    let nunit3TestAssemblies = !! (sprintf "Src/AutoFixture.NUnit3.UnitTest/bin/%s/Ploeh.AutoFixture.NUnit3.UnitTest.dll" configuration)

    nunit3TestAssemblies
    |> NUnit3 (fun p -> { p with StopOnError = false
                                 ResultSpecs = ["NUnit3TestResult.xml;format=nunit2"] })
)

Target "BuildAndTestOnly" (fun _ -> ())
Target "Build" (fun _ -> ())
Target "Test"  (fun _ -> ())

Target "CopyToReleaseFolder" (fun _ ->
    let buildOutput = [
      "Src/AutoFixture/bin/Release/Ploeh.AutoFixture.dll";
      "Src/AutoFixture/bin/Release/Ploeh.AutoFixture.pdb";
      "Src/AutoFixture/bin/Release/Ploeh.AutoFixture.XML";
      "Src/SemanticComparison/bin/Release/Ploeh.SemanticComparison.dll";
      "Src/SemanticComparison/bin/Release/Ploeh.SemanticComparison.pdb";
      "Src/SemanticComparison/bin/Release/Ploeh.SemanticComparison.XML";
      "Src/AutoMoq/bin/Release/Ploeh.AutoFixture.AutoMoq.dll";
      "Src/AutoMoq/bin/Release/Ploeh.AutoFixture.AutoMoq.pdb";
      "Src/AutoMoq/bin/Release/Ploeh.AutoFixture.AutoMoq.XML";
      "Src/AutoRhinoMock/bin/Release/Ploeh.AutoFixture.AutoRhinoMock.dll";
      "Src/AutoRhinoMock/bin/Release/Ploeh.AutoFixture.AutoRhinoMock.pdb";
      "Src/AutoRhinoMock/bin/Release/Ploeh.AutoFixture.AutoRhinoMock.XML";
      "Src/AutoFakeItEasy/bin/Release/Ploeh.AutoFixture.AutoFakeItEasy.dll";
      "Src/AutoFakeItEasy/bin/Release/Ploeh.AutoFixture.AutoFakeItEasy.pdb";
      "Src/AutoFakeItEasy/bin/Release/Ploeh.AutoFixture.AutoFakeItEasy.XML";
      "Src/AutoFakeItEasy2/bin/Release/Ploeh.AutoFixture.AutoFakeItEasy2.dll";
      "Src/AutoFakeItEasy2/bin/Release/Ploeh.AutoFixture.AutoFakeItEasy2.pdb";
      "Src/AutoFakeItEasy2/bin/Release/Ploeh.AutoFixture.AutoFakeItEasy2.XML";
      "Src/AutoNSubstitute/bin/Release/Ploeh.AutoFixture.AutoNSubstitute.dll";
      "Src/AutoNSubstitute/bin/Release/Ploeh.AutoFixture.AutoNSubstitute.pdb";
      "Src/AutoNSubstitute/bin/Release/Ploeh.AutoFixture.AutoNSubstitute.XML";
      "Src/AutoFoq/bin/Release/Ploeh.AutoFixture.AutoFoq.dll";
      "Src/AutoFoq/bin/Release/Ploeh.AutoFixture.AutoFoq.pdb";
      "Src/AutoFoq/bin/Release/Ploeh.AutoFixture.AutoFoq.XML";
      "Src/AutoFixture.xUnit.net/bin/Release/Ploeh.AutoFixture.Xunit.dll";
      "Src/AutoFixture.xUnit.net/bin/Release/Ploeh.AutoFixture.Xunit.pdb";
      "Src/AutoFixture.xUnit.net/bin/Release/Ploeh.AutoFixture.Xunit.XML";
      "Src/AutoFixture.xUnit.net2/bin/Release/Ploeh.AutoFixture.Xunit2.dll";
      "Src/AutoFixture.xUnit.net2/bin/Release/Ploeh.AutoFixture.Xunit2.pdb";
      "Src/AutoFixture.xUnit.net2/bin/Release/Ploeh.AutoFixture.Xunit2.XML";
      "Src/AutoFixture.NUnit2/bin/Release/Ploeh.AutoFixture.NUnit2.dll";
      "Src/AutoFixture.NUnit2/bin/Release/Ploeh.AutoFixture.NUnit2.pdb";
      "Src/AutoFixture.NUnit2/bin/Release/Ploeh.AutoFixture.NUnit2.XML";
      "Src/AutoFixture.NUnit2/bin/Release/Ploeh.AutoFixture.NUnit2.Addins.dll";
      "Src/AutoFixture.NUnit2/bin/Release/Ploeh.AutoFixture.NUnit2.Addins.pdb";
      "Src/AutoFixture.NUnit2/bin/Release/Ploeh.AutoFixture.NUnit2.Addins.XML";
      "Src/AutoFixture.NUnit3/bin/Release/Ploeh.AutoFixture.NUnit3.dll";
      "Src/AutoFixture.NUnit3/bin/Release/Ploeh.AutoFixture.NUnit3.pdb";
      "Src/AutoFixture.NUnit3/bin/Release/Ploeh.AutoFixture.NUnit3.XML";
      "Src/Idioms/bin/Release/Ploeh.AutoFixture.Idioms.dll";
      "Src/Idioms/bin/Release/Ploeh.AutoFixture.Idioms.pdb";
      "Src/Idioms/bin/Release/Ploeh.AutoFixture.Idioms.XML";
      "Src/Idioms.FsCheck/bin/Release/Ploeh.AutoFixture.Idioms.FsCheck.dll";
      "Src/Idioms.FsCheck/bin/Release/Ploeh.AutoFixture.Idioms.FsCheck.pdb";
      "Src/Idioms.FsCheck/bin/Release/Ploeh.AutoFixture.Idioms.FsCheck.XML";
      nunitToolsFolder @@ "lib/nunit.core.interfaces.dll"
    ]
    let nuGetPackageScripts = !! "NuGet/*.ps1" ++ "NuGet/*.txt" ++ "NuGet/*.pp" |> List.ofSeq
    let releaseFiles = buildOutput @ nuGetPackageScripts

    releaseFiles
    |> CopyFiles releaseFolder
)

Target "CleanNuGetPackages" (fun _ ->
    CleanDir nuGetOutputFolder
)

Target "NuGetPack" (fun _ ->
    let version = FileVersionInfo.GetVersionInfo("Src/AutoFixture/bin/Release/Ploeh.AutoFixture.dll").ProductVersion

    let nuSpecFiles = !! "NuGet/*.nuspec"

    nuSpecFiles
    |> Seq.iter (fun f -> NuGet (fun p -> { p with Version = version
                                                   WorkingDir = releaseFolder
                                                   OutputPath = nuGetOutputFolder
                                                   SymbolPackage = NugetSymbolPackage.Nuspec }) f)
)

let publishPackagesToNuGet apiFeed symbolFeed nugetKey =
    let packages = !! (sprintf "%s/*.nupkg" nuGetOutputFolder)

    packages
    |> Seq.map (fun p ->
        let isSymbolPackage = p.EndsWith "symbols.nupkg"
        let feed =
            match isSymbolPackage with
            | true -> symbolFeed
            | false -> apiFeed

        let meta = GetMetaDataFromPackageFile p
        let version = 
            match isSymbolPackage with
            | true -> sprintf "%s.symbols" meta.Version
            | false -> meta.Version

        (meta.Id, version, feed))
    |> Seq.iter (fun (id, version, feed) -> NuGetPublish (fun p -> { p with PublishUrl = feed
                                                                            AccessKey = nugetKey
                                                                            OutputPath = nuGetOutputFolder
                                                                            Project = id
                                                                            Version = version }))

Target "PublishNuGetPreReleaseOnly" (fun _ -> publishPackagesToNuGet 
                                                "https://www.myget.org/F/autofixture/api/v2/package" 
                                                "https://www.myget.org/F/autofixture/symbols/api/v2/package"
                                                (getBuildParam "NuGetPreReleaseKey"))

Target "PublishNuGetReleaseOnly" (fun _ -> publishPackagesToNuGet
                                             "https://www.nuget.org/api/v2/package"
                                             "https://nuget.smbsrc.net/"
                                             (getBuildParam "NuGetReleaseKey"))

Target "CompleteBuild"          (fun _ -> ())
Target "PublishNuGetPreRelease" (fun _ -> ())
Target "PublishNuGetRelease"    (fun _ -> ())
Target "PublishNuGetAll"        (fun _ -> ())

"CleanVerify"  ==> "CleanAll"
"CleanRelease" ==> "CleanAll"

"CleanReleaseFolder" ==> "Verify"
"CleanAll"           ==> "Verify"

"Verify"                ==> "Build"
"PatchAssemblyVersions" ==> "Build"
"BuildOnly"             ==> "Build"

"Build"    ==> "Test"
"TestOnly" ==> "Test"

"BuildOnly" 
    ==> "TestOnly"
    ==> "BuildAndTestOnly"

"Test" ==> "CopyToReleaseFolder"

"CleanNuGetPackages"  ==> "NuGetPack"
"CopyToReleaseFolder" ==> "NuGetPack"

"NuGetPack" ==> "CompleteBuild"

"NuGetPack"               ==> "PublishNuGetRelease"
"PublishNuGetReleaseOnly" ==> "PublishNuGetRelease"

"NuGetPack"                  ==> "PublishNuGetPreRelease"
"PublishNuGetPreReleaseOnly" ==> "PublishNuGetPreRelease"

"PublishNuGetRelease"    ==> "PublishNuGetAll"
"PublishNuGetPreRelease" ==> "PublishNuGetAll"

RunTargetOrDefault "CompleteBuild"
