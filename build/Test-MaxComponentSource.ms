-- Run only in a fresh 3dsmaxbatch process, with no input scene.
(
local output = maxOps.mxsCmdLineArgs[#output]
if output == undefined do throw "Missing output argument."
if doesFileExist output do throw "Refusing to overwrite a test output."
if objects.count != 0 do throw "Expected a new empty batch scene."
units.SystemType = #Millimeters
units.SystemScale = 1
local sample = box name:"static_frame_sample" width:1200 length:100 height:1600 pos:[600,-50,0]
select sample
exportFile output #noPrompt selectedOnly:true using:GLTF_Export
if not doesFileExist output do throw "GLB exporter did not create the file."
format "MAX_COMPONENT_SOURCE_NATIVE_OK %\n" output
)
