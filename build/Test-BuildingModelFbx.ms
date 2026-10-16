-- Run in a fresh Max 2024 Batch process, with a disposable exported house only.
(
    local input = maxOps.mxsCmdLineArgs[#input]
    local output = maxOps.mxsCmdLineArgs[#output]
    if input == undefined or output == undefined do throw "Missing input/output."
    if objects.count != 0 do throw "Refusing to change a nonempty scene."
    if doesFileExist output do throw "Refusing to overwrite an existing MAX file."
    units.SystemType = #Millimeters
    units.SystemScale = 1
    pluginManager.loadClass FBXIMP
    FBXImporterSetParam "ResetImport"
    for setting in #(#("ConvertUnit", "mm"), #("Animation", false), #("SmoothingGroups", false), #("Mode", #create)) do
        if FBXImporterSetParam setting[1] setting[2] == unsupplied do throw ("Unsupported FBX option: " + setting[1])
    importFile input #noPrompt using:FBXIMP
    local nodes = geometry as array
    if nodes.count != 16 do throw ("Expected 16 element nodes, got " + nodes.count as string)
    local low = [1e20,1e20,1e20]
    local high = [-1e20,-1e20,-1e20]
    for node in nodes do
    (
        local bounds = nodeGetBoundingBox node (matrix3 1)
        for axis = 1 to 3 do
        (
            low[axis] = amin low[axis] bounds[1][axis]
            high[axis] = amax high[axis] bounds[2][axis]
        )
    )
    local size = high-low
    if distance size [7440,5640,6900] > 0.05 do throw ("Incorrect millimetre dimensions: " + size as string)
    if not saveMaxFile output quiet:true do throw "MAX save failed."
    if not loadMaxFile output quiet:true do throw "MAX reopen failed."
    if geometry.count != 16 do throw "MAX reopen lost element nodes."
    format "MAX2024_FBX_IMPORT_OK elements=% dimensionsMm=% savedAndReopened=%\n" geometry.count size output
)
