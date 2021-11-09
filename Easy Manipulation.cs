/* Easy Manipulation by Onyx Blackstone
 *  
 * Do not edit this script!
 * All options are available in this PB's Custom Data.
 * For full manual go to https://github.com/Onyx47/EasyManipulation/wiki
 * 
 */

private readonly ArmController _controller;
private static Program _program;

public enum ControlAxis
{
    None,
    RotX,
    RotY,
    MovX,
    MovY,
    MovZ,
    Roll
}

private readonly Dictionary<ControlAxis, List<string>> ControlMap = new Dictionary<ControlAxis, List<string>>
{
    { ControlAxis.RotX, new List<string> {"↑", "↓"} },
    { ControlAxis.RotY, new List<string> {"←", "→"} },
    { ControlAxis.MovX, new List<string> {"A", "D"} },
    { ControlAxis.MovY, new List<string> {"C", "_"} },
    { ControlAxis.MovZ, new List<string> {"W", "S"} },
    { ControlAxis.Roll, new List<string> {"Q", "E"} }
};

// Global configuration, contains only the tag and index of screen surface to draw to
public struct GlobalConfiguration
{
    public string Tag;
    public int UseCockpitScreen;
    public bool FastPoseRestore;
    public bool PauseLocksJoints;
}

// Segment configuration, contains all sensitivity, speed and control settings
public struct SegmentConfiguration
{
    public float RotorSensitivity;
    public float RotorMaxSpeed;
    public float RotorMaxOffsetFactor;
    public float PistonSensitivity;
    public float PistonMaxSpeed;
    public float PistonMaxOffsetFactor;
    public bool UseMouse;
    public bool UseUpDown;
    public bool UseLeftRight;
    public bool UseWS;
    public bool UseAD;
    public bool UseQE;
    public bool UseCSpace;
    public bool InvertUpDown;
    public bool InvertLeftRight;
    public bool InvertWS;
    public bool InvertAD;
    public bool InvertQE;
    public bool InvertCSpace;
}

// Group configuration, contains only sensitivity and speed settings
// Overrides segment settings
public struct GroupConfiguration
{
    public float RotorSensitivity;
    public float RotorMaxSpeed;
    public float RotorMaxOffsetFactor;
    public float PistonSensitivity;
    public float PistonMaxSpeed;
    public float PistonMaxOffsetFactor;
    public bool MirrorGroup;
}

// Class representing the entire arm, includes all the segments
public class Arm
{
    public readonly Dictionary<string, ArmSegment> Segments = new Dictionary<string, ArmSegment>();
    public readonly Dictionary<string, ArmGroup> Groups = new Dictionary<string, ArmGroup>();
    public List<IMyTerminalBlock> MainTerminalGroup { get; } = new List<IMyTerminalBlock>();
    public readonly Dictionary<string, List<IMyTerminalBlock>> GroupTerminalGroups;
    public readonly Dictionary<string, List<IMyTerminalBlock>> SegmentTerminalGroups = new Dictionary<string, List<IMyTerminalBlock>>();
    public readonly string Tag;
    public ArmSegment ActiveSegment;
    public IMyShipController Controller { get; }

    public Arm(GlobalConfiguration configuration, IMyShipController controller)
    {
        Tag = configuration.Tag;
        Controller = controller;

        var defaultSegment = new ArmSegment(this, "Main");

        if (!Segments.ContainsKey("Main"))
            Segments.Add("Main", defaultSegment);

        ActiveSegment = defaultSegment;
        
        // Get the main terminal group for this arm
        var mainGroup = Util.GetMainGroup(Tag);
        mainGroup.GetBlocks(MainTerminalGroup, block => Util.BlockIsRotor(block) || Util.BlockIsPiston(block));
        
        // Get all the segment groups
        var segmentGroups = Util.GetSegmentGroups(Tag);
        foreach (var segmentGroup in segmentGroups)
        {
            var blocks = new List<IMyTerminalBlock>();
            segmentGroup.Value.GetBlocks(blocks, block => Util.BlockIsRotor(block) || Util.BlockIsPiston(block));
            
            SegmentTerminalGroups.Add(segmentGroup.Key, blocks);
        }

        // Create the segments
        foreach (var segmentTerminalGroup in SegmentTerminalGroups)
        {
            AddSegment(segmentTerminalGroup.Key);
        }
        
        // Get all the group groups
        GroupTerminalGroups = Util.GetGroupGroups(Tag);
        
        // Create the groups
        foreach (var groupTerminalGroup in GroupTerminalGroups)
        {
            AddGroup(new ArmGroup(this, groupTerminalGroup.Key));
        }

        // Get all the lights in the main group
        var lightBlocks = new List<IMyLightingBlock>();
        mainGroup.GetBlocksOfType(lightBlocks);
        
        // Assign lights to segments
        foreach (var segmentGroup in segmentGroups)
        {
            var segmentLights = new List<IMyLightingBlock>();
            segmentGroup.Value.GetBlocksOfType(segmentLights);

            foreach (var segmentLight in segmentLights.Where(segmentLight => lightBlocks.Contains(segmentLight)))
            {
                lightBlocks.Remove(segmentLight);
            }
            
            Segments[segmentGroup.Key].AddLights(segmentLights);
        }

        // Assign all lights not part of a certain segment to default segment
        Segments["Main"].AddLights(lightBlocks);
    }

    private void AddGroup(ArmGroup group)
    {
        if (Groups.ContainsKey(group.Name)) return;

        Groups.Add(group.Name, group);
    }

    public ArmGroup GetGroup(string name)
    {
        return Groups.ContainsKey(name) ? Groups[name] : null;
    }

    private void AddSegment(string name)
    {
        var segment = new ArmSegment(this, name);
        AddSegment(segment);
    }

    private void AddSegment(ArmSegment segment)
    {
        if (!Segments.ContainsKey(segment.Name))
            Segments.Add(segment.Name, segment);
    }

    public ArmSegment GetSegment(string name)
    {
        return Segments.ContainsKey(name) ? Segments[name] : null;
    }
    // Sets the active segment
    public void SetActiveSegment(string name)
    {
        var activeSegment = GetSegment(name);
        if(activeSegment == null) return;

        ActiveSegment = activeSegment;
        
        // Toggle lights for segments
        foreach (var segment in Segments)
        {
            foreach (var light in segment.Value.Lights)
            {
                light.Enabled = segment.Value == ActiveSegment;
            }
        }
    }
}

// Class representing an arm segment
public class ArmSegment
{
    private readonly Arm _arm;
    public string Name { get; }
    public SegmentConfiguration Configuration;
    public readonly List<ArmGroup> Groups = new List<ArmGroup>();
    public readonly List<Joint> Joints = new List<Joint>();
    public readonly List<IMyLightingBlock> Lights = new List<IMyLightingBlock>();
    private readonly List<ControlAxis> _usedAxes = new List<ControlAxis>();

    public ArmSegment(Arm arm, string name)
    {
        _arm = arm;
        Name = name;
        Configuration = GetSegmentConfiguration();
    }

    // Adds a joint to the segment
    public void AddJoint(Joint joint)
    {
        if (Joints.Contains(joint)) return;

        var block = joint.GetBlock();
        joint.RelativeDirection =
            _arm.Controller.WorldMatrix.GetClosestDirection(block.WorldMatrix.Up);

        var controlAxis = ControlAxis.None;

        if (joint.Group != null)
        {
            if (joint.Group.Joints.Count > 1)
            {
                controlAxis = joint.Group.Joints.First().ControlAxis;
            }
            
            if(!Groups.Contains(joint.Group)) Groups.Add(joint.Group);
        }

        if (controlAxis == ControlAxis.None) controlAxis = GetAxisForDirection(joint.RelativeDirection, block);

        joint.ControlAxis = controlAxis;
        Joints.Add(joint);
        joint.Inverted =
            joint.RelativeDirection == Base6Directions.Direction.Down ||
            joint.RelativeDirection == Base6Directions.Direction.Left ||
            joint.RelativeDirection == Base6Directions.Direction.Forward;

        // Additionally, invert the control another time if the group is mirrored
        if (joint.Group != null && joint.Group.Configuration.MirrorGroup)
        {
            if (Util.BlockIsPiston(joint.GetBlock()))
            {
                // For pistons, only invert if the joint is not oriented the same as the first joint in the group
                joint.BlockInverted = 
                    joint.Group.Joints.First().RelativeDirection != joint.RelativeDirection;
            }
            else
            {
                // Invert rotors if they are at the same distance
                var otherRotors = Joints.Where(
                    c => c != joint &&
                            Util.BlockIsRotor(c.GetBlock()) &&
                            c.Distance == joint.Distance);

                if (otherRotors.Any()) joint.BlockInverted = true;
            }
        }
    }

    public void AddLights(List<IMyLightingBlock> lights)
    {
        Lights.AddRange(lights);
    }

    // Gets the next valid axis for a block based on its direction and type
    private ControlAxis GetAxisForDirection(Base6Directions.Direction direction, IMyTerminalBlock block)
    {
        var validAxes = Util.AxesForDirection(direction, block);

        foreach (var axis in validAxes)
        {
            if (_usedAxes.Contains(axis)) continue;

            switch (axis)
            {
                case ControlAxis.RotX:
                {
                    if (Configuration.UseLeftRight == false) continue;
                    break;
                }
                case ControlAxis.RotY:
                {
                    if (Configuration.UseUpDown == false) continue;
                    break;
                }
                case ControlAxis.MovX:
                {
                    if (Configuration.UseAD == false) continue;
                    break;
                }
                case ControlAxis.MovY:
                {
                    if (Configuration.UseCSpace == false) continue;
                    break;
                }
                case ControlAxis.MovZ:
                {
                    if (Configuration.UseWS == false) continue;
                    break;
                }
                case ControlAxis.Roll:
                {
                    if (Configuration.UseQE == false) continue;
                    break;
                }
                default: continue;
            }

            _usedAxes.Add(axis);
            return axis;
        }

        return ControlAxis.None;
    }

    // Gets the configuration for a segment
    public SegmentConfiguration GetSegmentConfiguration()
    {
        var ini = new MyIni();
        var config = _program.Storage;
        var sectionName = $"{_arm.Tag}/Segments/{Name}";

        var configuration = new SegmentConfiguration
        {
            RotorSensitivity = 1.0f,
            RotorMaxSpeed = 10.0f,
            RotorMaxOffsetFactor = 0.0f,
            PistonSensitivity = 10.0f,
            PistonMaxSpeed = 1.0f,
            PistonMaxOffsetFactor = 0.0f,
            UseMouse = true,
            UseUpDown = true,
            UseLeftRight = true,
            UseWS = true,
            UseAD = true,
            UseQE = true,
            UseCSpace = true,
            InvertUpDown = false,
            InvertLeftRight = false,
            InvertWS = false,
            InvertAD = false,
            InvertQE = false,
            InvertCSpace = false,
        };

        if (!ini.TryParse(config) || !ini.ContainsSection(sectionName)) return configuration;

        if (ini.ContainsKey(sectionName, "RotorSensitivity"))
            configuration.RotorSensitivity = ini.Get(sectionName, "RotorSensitivity").ToSingle();

        if (ini.ContainsKey(sectionName, "RotorMaxSpeed"))
            configuration.RotorMaxSpeed = ini.Get(sectionName, "RotorMaxSpeed").ToSingle();
        
        if (ini.ContainsKey(sectionName, "RotorMaxOffsetFactor"))
            configuration.RotorMaxOffsetFactor = ini.Get(sectionName, "RotorMaxOffsetFactor").ToSingle();

        if (ini.ContainsKey(sectionName, "PistonSensitivity"))
            configuration.PistonSensitivity = ini.Get(sectionName, "PistonSensitivity").ToSingle();

        if (ini.ContainsKey(sectionName, "PistonMaxSpeed"))
            configuration.PistonMaxSpeed = ini.Get(sectionName, "PistonMaxSpeed").ToSingle();
        
        if (ini.ContainsKey(sectionName, "PistonMaxOffsetFactor"))
            configuration.PistonMaxOffsetFactor = ini.Get(sectionName, "PistonMaxOffsetFactor").ToSingle();

        if (ini.ContainsKey(sectionName, "UseMouse"))
            configuration.UseMouse = ini.Get(sectionName, "UseMouse").ToBoolean();

        if (ini.ContainsKey(sectionName, "UseUpDown"))
            configuration.UseUpDown = ini.Get(sectionName, "UseUpDown").ToBoolean();

        if (ini.ContainsKey(sectionName, "UseLeftRight"))
            configuration.UseLeftRight = ini.Get(sectionName, "UseLeftRight").ToBoolean();

        if (ini.ContainsKey(sectionName, "UseWS"))
            configuration.UseWS = ini.Get(sectionName, "UseWS").ToBoolean();

        if (ini.ContainsKey(sectionName, "UseAD"))
            configuration.UseAD = ini.Get(sectionName, "UseAD").ToBoolean();

        if (ini.ContainsKey(sectionName, "UseQE"))
            configuration.UseQE = ini.Get(sectionName, "UseQE").ToBoolean();

        if (ini.ContainsKey(sectionName, "UseCSpace"))
            configuration.UseCSpace = ini.Get(sectionName, "UseCSpace").ToBoolean();

        if (ini.ContainsKey(sectionName, "InvertUpDown"))
            configuration.InvertUpDown = ini.Get(sectionName, "InvertUpDown").ToBoolean();

        if (ini.ContainsKey(sectionName, "InvertLeftRight"))
            configuration.InvertLeftRight = ini.Get(sectionName, "InvertLeftRight").ToBoolean();

        if (ini.ContainsKey(sectionName, "InvertWS"))
            configuration.InvertWS = ini.Get(sectionName, "InvertWS").ToBoolean();

        if (ini.ContainsKey(sectionName, "InvertAD"))
            configuration.InvertAD = ini.Get(sectionName, "InvertAD").ToBoolean();

        if (ini.ContainsKey(sectionName, "InvertQE"))
            configuration.InvertQE = ini.Get(sectionName, "InvertQE").ToBoolean();

        if (ini.ContainsKey(sectionName, "InvertCSpace"))
            configuration.InvertCSpace = ini.Get(sectionName, "InvertCSpace").ToBoolean();

        return configuration;
    }
}

// Class representing an arm group
public class ArmGroup
{
    private readonly Arm _arm;
    public string Name { get; }
    public List<Joint> Joints { get; } = new List<Joint>();
    public GroupConfiguration Configuration;
    
    public ArmGroup(Arm arm, string name)
    {
        _arm = arm;
        Name = name;
        Configuration = GetGroupConfiguration();
    }

    public void AddJoint(Joint joint)
    {
        if (!Joints.Contains(joint)) Joints.Add(joint);
    }

    public GroupConfiguration GetGroupConfiguration()
    {
        var ini = new MyIni();
        var config = _program.Storage;
        var sectionName = $"{_arm.Tag}/Groups/{Name}";

        var configuration = new GroupConfiguration
        {
            RotorSensitivity = 1.0f,
            RotorMaxSpeed = 10.0f,
            RotorMaxOffsetFactor = 0.0f,
            PistonSensitivity = 5.0f,
            PistonMaxSpeed = 1.0f,
            PistonMaxOffsetFactor = 0.0f,
            MirrorGroup = false
        };
        
        if (!ini.TryParse(config) || !ini.ContainsSection(sectionName)) return configuration;

        if (ini.ContainsKey(sectionName, "RotorSensitivity"))
            configuration.RotorSensitivity = ini.Get(sectionName, "RotorSensitivity").ToSingle();

        if (ini.ContainsKey(sectionName, "RotorMaxSpeed"))
            configuration.RotorMaxSpeed = ini.Get(sectionName, "RotorMaxSpeed").ToSingle();
        
        if (ini.ContainsKey(sectionName, "RotorMaxOffsetFactor"))
            configuration.RotorMaxOffsetFactor = ini.Get(sectionName, "RotorMaxOffsetFactor").ToSingle();

        if (ini.ContainsKey(sectionName, "PistonSensitivity"))
            configuration.PistonSensitivity = ini.Get(sectionName, "PistonSensitivity").ToSingle();

        if (ini.ContainsKey(sectionName, "PistonMaxSpeed"))
            configuration.PistonMaxSpeed = ini.Get(sectionName, "PistonMaxSpeed").ToSingle();
        
        if (ini.ContainsKey(sectionName, "PistonMaxOffsetFactor"))
            configuration.PistonMaxOffsetFactor = ini.Get(sectionName, "PistonMaxOffsetFactor").ToSingle();
        
        if (ini.ContainsKey(sectionName, "MirrorGroup"))
            configuration.MirrorGroup = ini.Get(sectionName, "MirrorGroup").ToBoolean();

        return configuration;
    }
}

// Class representing an arm joint (rotor or piston)
public class Joint
{
    private readonly Arm _arm;
    public ArmGroup Group { get; }
    public bool Inverted;
    public bool BlockInverted;
    public ControlAxis ControlAxis;
    public Base6Directions.Direction RelativeDirection;
    private float DesiredValue { get; set; }
    private readonly IMyTerminalBlock Block;
    public int Distance { get; }
    public float Pose
    {
        get
        {
            var block = Block as IMyMotorStator;
            return block != null
                ? MathHelper.ToDegrees(block.Angle)
                : ((IMyPistonBase) Block).CurrentPosition;
        }
    }

    // Joint constructor
    public Joint(IMyTerminalBlock block, Arm arm, int distance = 0)
    {
        _arm = arm;
        Distance = distance;
        Block = block;
        DesiredValue = Pose;
        
        // Check if this joint already exists
        if (arm.Segments.Select(segment => segment.Value.Joints.Where(c => c.Block == block)).Any(joints => joints.Any()))
        {
            return;
        }

        // Check if this joint is a part of any group
        foreach (var terminalGroup in arm.GroupTerminalGroups.Where(terminalGroup => terminalGroup.Value.Contains(block)))
        {
            if(Group != null) throw new Exception(
                $"Block {Block.CustomName} is a part of multiple groups. " +
                "Blocks can only be a part of a single group.");
            
            Group = arm.GetGroup(terminalGroup.Key);
            Group.AddJoint(this);
        }

        // Get the block segment
        var terminalSegments =
            arm.SegmentTerminalGroups.Where(terminalGroup => terminalGroup.Value.Contains(block)).ToList();

        if (!terminalSegments.Any())
        {
            arm.GetSegment("Main")?.AddJoint(this);
        }
        else
        {
            foreach (var terminalGroup in terminalSegments)
            {
                arm.GetSegment(terminalGroup.Key)?.AddJoint(this);
            }
        }
    }

    // Returns the underlying IMyTerminalBlock
    public IMyTerminalBlock GetBlock()
    {
        return Block;
    }
    
    // Returns the underlying block with type
    private T GetBlock<T>() where T: class
    {
        if ((T)Block != null &&
            Block.WorldMatrix.Translation != new Vector3D(0, 0, 0))
            return (T) Block;

        return default(T);
    }

    // Stops movement of joint sub grid (piston or rotor head)
    public void Stop()
    {
        // Block has been deleted
        if(Block.WorldMatrix.Translation == Vector3D.Zero) return;
        
        if (Block is IMyMotorStator)
        {
            GetBlock<IMyMotorStator>().TargetVelocityRPM = 0.0f;
        }
        else if(Block is IMyPistonBase)
        {
            GetBlock<IMyPistonBase>().Velocity = 0.0f;
        }
    }

    // Tries to move the piston or rotor head to the desired position / angle
    public void MoveToPosition(float position)
    {
        if (Block is IMyMotorStator)
        {
            var block = GetBlock<IMyMotorStator>();

            var rotorMaxSpeed = _arm.ActiveSegment.Configuration.RotorMaxSpeed;

            if (Group != null) rotorMaxSpeed = Group.Configuration.RotorMaxSpeed;

            var currentValue = MathHelper.ToDegrees(block.Angle);
            DesiredValue = position % 360;

            if (!IsAtPosition())
            {
                var turnAngle = Util.AngleDifference(DesiredValue, currentValue);
                var spinSpeed =
                    MathHelper.Clamp(turnAngle, -Math.Abs(rotorMaxSpeed), Math.Abs(rotorMaxSpeed));

                block.TargetVelocityRPM =
                    Math.Abs(turnAngle) < Math.Abs(spinSpeed) ? turnAngle * 0.5f : spinSpeed;
            }
            else
            {
                block.TargetVelocityRPM = 0;
            }
        }
        else
        {
            var block = GetBlock<IMyPistonBase>();
            if (block == default(IMyPistonBase)) return;

            var pistonMaxSpeed = _arm.ActiveSegment.Configuration.PistonMaxSpeed;

            if (Group != null) pistonMaxSpeed = Group.Configuration.PistonMaxSpeed;

            DesiredValue = position;

            if (!IsAtPosition())
                block.Velocity = MathHelper.Clamp(
                    DesiredValue - block.CurrentPosition,
                    -Math.Abs(pistonMaxSpeed),
                    Math.Abs(pistonMaxSpeed)
                );
            else
                block.Velocity = 0.0f;
        }
    }

    public void Move(float amount, bool invertControls = false)
    {
        if (Block is IMyMotorStator)
        {
            var block = GetBlock<IMyMotorStator>();
            if(block == default(IMyMotorStator)) return;

            var rotorSensitivity = _arm.ActiveSegment.Configuration.RotorSensitivity;
            var rotorMaxSpeed = _arm.ActiveSegment.Configuration.RotorMaxSpeed;
            var rotorMaxOffsetFactor = _arm.ActiveSegment.Configuration.RotorMaxOffsetFactor;

            if (Group != null)
            {
                rotorSensitivity = Group.Configuration.RotorSensitivity;
                rotorMaxSpeed = Group.Configuration.RotorMaxSpeed;
                rotorMaxOffsetFactor = Group.Configuration.RotorMaxOffsetFactor;
            }
            
            if(rotorSensitivity == 0.0f) return;

            DesiredValue += MathHelper.Clamp(
                                    amount, -Math.Abs(rotorSensitivity), 
                                    Math.Abs(rotorSensitivity)
                                    ) * (Inverted ? -1 : 1) * (invertControls ? -1 : 1) * (BlockInverted ? -1 : 1)
                                % 360;
            
            // Clamp desired movement within rotor limits
            DesiredValue = MathHelper.Clamp(DesiredValue, block.LowerLimitDeg, block.UpperLimitDeg);
            
            // Don't allow offset higher than the max offset factor
            if (rotorMaxOffsetFactor > 0 && Math.Abs(Math.Abs(DesiredValue) - Math.Abs(Pose)) >
                rotorMaxOffsetFactor * rotorMaxSpeed)
            {
                if (DesiredValue > Pose)
                    DesiredValue = Pose + rotorMaxOffsetFactor * rotorMaxSpeed;
                else
                    DesiredValue = Pose - rotorMaxOffsetFactor * rotorMaxSpeed;
            }

            if (!IsAtPosition())
            {
                var turnAngle = Util.AngleDifference(DesiredValue, Pose);
                var spinSpeed =
                    MathHelper.Clamp(turnAngle, -Math.Abs(rotorMaxSpeed), Math.Abs(rotorMaxSpeed));

                block.TargetVelocityRPM = Math.Abs(turnAngle) < Math.Abs(spinSpeed) ? turnAngle * 0.5f : spinSpeed;
            }
            else
            {
                block.TargetVelocityRPM = 0;
            }
        }
        else if (Block is IMyPistonBase)
        {
            var block = GetBlock<IMyPistonBase>();
            if(block == default(IMyPistonBase)) return;
            
            var pistonSensitivity = _arm.ActiveSegment.Configuration.PistonSensitivity;
            var pistonMaxSpeed = _arm.ActiveSegment.Configuration.PistonMaxSpeed;
            var pistonMaxOffsetFactor = _arm.ActiveSegment.Configuration.PistonMaxOffsetFactor;

            if (Group != null)
            {
                pistonSensitivity = Group.Configuration.PistonSensitivity;
                pistonMaxSpeed = Group.Configuration.PistonMaxSpeed;
                pistonMaxOffsetFactor = Group.Configuration.PistonMaxOffsetFactor;
            }
            
            // Scale down the keyboard input into -1 <= x <= 1 range
            if (Math.Abs(amount) == 9) amount /= Math.Abs(amount);

            if (!BlockInverted)
            {
                DesiredValue = MathHelper.Clamp(
                    block.CurrentPosition + amount * Math.Abs(pistonSensitivity) *
                    (Inverted ? -1 : 1) * (invertControls ? -1 : 1),
                    block.MinLimit,
                    block.MaxLimit
                );
            }
            else if (BlockInverted)
            {
                DesiredValue = MathHelper.Clamp(
                    block.CurrentPosition - amount * Math.Abs(pistonSensitivity) *
                    (Inverted ? -1 : 1) * (invertControls ? -1 : 1),
                    block.MinLimit,
                    block.MaxLimit
                );
            }
            
            // Don't allow offset higher than the max offset factor
            if (pistonMaxOffsetFactor > 0 && Math.Abs(Math.Abs(DesiredValue) - Math.Abs(block.CurrentPosition)) >
                pistonMaxOffsetFactor * pistonMaxSpeed)
            {
                if (DesiredValue > block.CurrentPosition)
                    DesiredValue = block.CurrentPosition + pistonMaxOffsetFactor * pistonMaxSpeed;
                else
                    DesiredValue = block.CurrentPosition - pistonMaxOffsetFactor * pistonMaxSpeed;
            }

            if (!IsAtPosition())
            {
                block.Velocity = MathHelper.Clamp(
                    DesiredValue - block.CurrentPosition,
                    -Math.Abs(pistonMaxSpeed),
                    Math.Abs(pistonMaxSpeed)
                );
            }
            else
            {
                block.Velocity = 0.0f;
            }
        }
    }

    private bool IsAtPosition()
    {
        return IsAtPosition(DesiredValue);
    }

    public bool IsAtPosition(float position)
    {
        if (!(Block is IMyMotorStator))
            return Math.Abs(position - ((IMyPistonBase) Block).CurrentPosition) < 0.1;
        
        position = (position + 360) % 360;
        var currentPosition = (Pose + 360) % 360;

        return Math.Abs(Math.Abs(position) - Math.Abs(currentPosition)) <= 0.1;

    }
}

// Class representing an arm controller
private class ArmController
{
    private readonly IMyShipController _controller;
    private GlobalConfiguration _configuration;
    private Arm _arm;
    private bool _autoPaused;
    private bool _manuallyPaused;
    private bool _restoring;
    private bool _toolMode;
    private string _restorePose;
    private ArmSegment _activeSegment;
    private readonly Dictionary<string, Dictionary<long, float>> _poses = new Dictionary<string, Dictionary<long, float>>();
    private readonly Dictionary<string, IMyTimerBlock> _timers = new Dictionary<string, IMyTimerBlock>();
    private readonly List<IMyTextSurface> _screens;
    private List<Joint> _manuallyLockedJoints;

    public ArmController(GlobalConfiguration configuration, IMyShipController controller)
    {
        _configuration = configuration;
        _controller = controller;
        _arm = new Arm(configuration, _controller);
        _autoPaused = false;
        _manuallyPaused = false;
        _restoring = false;
        _toolMode = false;
        _screens = new List<IMyTextSurface>();
        _manuallyLockedJoints = new List<Joint>();

        // Restore any poses saved in custom data
        if (controller.CustomData == string.Empty) return;
        
        var posesIni = new MyIni();
        if (!posesIni.TryParse(controller.CustomData)) return;
        
        var sections = new List<string>();
        posesIni.GetSections(sections);

        foreach (var section in sections)
        {
            if (section.IndexOf($"{_arm.Tag}/Poses/") != 0) continue;
            
            var parts = section.Split('/');
            if(parts.Length != 3) return;
            var poseName = parts[2];
            var poses = new List<MyIniKey>();
                
            _poses.Add(poseName, new Dictionary<long, float>());
            posesIni.GetKeys(section, poses);

            foreach (var pose in poses)
            {
                _poses[poseName].Add(long.Parse(pose.Name), posesIni.Get(pose).ToSingle());
            }
        }
        
        // Find any timers bound to saved poses
        var mainGroup = Util.GetMainGroup(_configuration.Tag);
        var timers = new List<IMyTimerBlock>();
        
        mainGroup.GetBlocksOfType(timers);
        foreach (var pose in _poses)
        {
            foreach (var timer in timers.Where(timer => timer.CustomName.Contains($"[{pose.Key}]")))
            {
                _timers.Add(pose.Key, timer);
            }
        }
        
        // Find any text surfaces
        _screens = new List<IMyTextSurface>();
        mainGroup.GetBlocksOfType(_screens);

        var cockpit = _controller as IMyCockpit;
        
        if (cockpit != null && 
            (configuration.UseCockpitScreen <= -1 || 
                cockpit.GetSurface(_configuration.UseCockpitScreen) != null))
        {
            var cockpitScreen = cockpit.GetSurface(_configuration.UseCockpitScreen);

            if (cockpitScreen != null) _screens.Add(cockpitScreen);
        }
    }

    // Creates the arm from a base block. Should only be called once on startup.
    public Arm CreateArm()
    {
        // Try to find a rotor or a piston the ship controller is a subgrid of
        var rootJoint = GetRootJoint(_controller);
    
        if (rootJoint == _controller)
        {
            // Controller is not on a subgrid, construct the arm using any matching blocks on controller grid
            var rootBlocks = new List<IMyTerminalBlock>();
        
            _program.GridTerminalSystem.GetBlocksOfType(rootBlocks, block =>
            {
                if (block.CubeGrid != _controller.CubeGrid) return false;
                return (Util.BlockIsRotor(block) || Util.BlockIsPiston(block)) && _arm.MainTerminalGroup.Contains(block);
            });

            foreach (var joint in rootBlocks.Select(block => new Joint(block, _arm)))
            {
                _buildArm(joint.GetBlock(), ref _arm, 1);
            }
        }
        else
        {
            var joint = new Joint(rootJoint, _arm);
            
            _buildArm(joint.GetBlock(), ref _arm, 0);

            if (joint.Group != null)
            {
                var terminalGroups = Util.GetGroupGroups(_arm.Tag);
                var group = terminalGroups.Where(g => g.Value.Contains(joint.GetBlock())).ToList();

                if (group.Count == 1)
                {
                    // Find the segment this joint is a part of
                    var segments = _arm.Segments.Where(s => s.Value.Joints.Contains(joint))
                        .ToList();

                    foreach (var block in group.First().Value)
                    {
                        if (block == joint.GetBlock()) continue;
                        
                        foreach (var segment in segments)
                        {
                            segment.Value.AddJoint(new Joint(block, _arm));
                        }
                    }
                }
            }
        }

        var joints = new List<Joint>();
        foreach (var segment in _arm.Segments)
        {
            joints.AddRange(segment.Value.Joints);
        }

        joints = joints.Distinct().ToList();
        
        _program.Echo($"Successfully built an arm containing {joints.Count} joints " +
                        $"in {_arm.Segments.Count} segments " +
                        $"and {_arm.Groups.Count} groups.");
        
        _activeSegment = _arm.GetSegment("Main");
        
        return _arm;
    }

    // Reloads the configuration
    public void ReloadConfiguration()
    {
        _configuration = _program.GetGlobalConfiguration();
        foreach (var segment in _arm.Segments.Values)
        {
            segment.Configuration = segment.GetSegmentConfiguration();
        }

        foreach (var group in _arm.Groups.Values)
        {
            group.Configuration = group.GetGroupConfiguration();
        }
    }
    
    // Changes the active segment
    public void SetActiveSegment(string name)
    {
        if (_arm.GetSegment(name) == null) return;
        
        _activeSegment = _arm.GetSegment(name);
        _arm.SetActiveSegment(name);
        UpdateDisplays();
    }

    // Toggles tool mode
    public void SetToolMode(bool enable)
    {
        _toolMode = enable;
        UpdateDisplays();
        
        _controller.ControlThrusters = !_toolMode;
        _controller.ControlWheels = !_toolMode;
        _controller.SetValueBool("ControlGyros", !_toolMode);
    }

    // Get the root joint of the whole arm
    private IMyTerminalBlock GetRootJoint(IMyTerminalBlock baseBlock)
    {
        var rootJoints = new List<IMyTerminalBlock>();

        // Try to find a rotor or a piston the ship controller is a subgrid of
        _program.GridTerminalSystem.GetBlocksOfType(rootJoints, block =>
        {
            if (!_arm.MainTerminalGroup.Contains(block)) return false;

            return 
                Util.BlockIsRotor(block)  && ((IMyMotorStator) block).TopGrid == baseBlock.CubeGrid ||
                Util.BlockIsPiston(block) && ((IMyPistonBase)  block).TopGrid == baseBlock.CubeGrid;
        });

        rootJoints.Sort((x, y) => x.EntityId.CompareTo(y.EntityId));

        return rootJoints.Count != 0 ? GetRootJoint(rootJoints.First()) : baseBlock;
    }
    
    // Get all currently locked joints
    private List<Joint> GetLockedJoints()
    {
        var lockedJoints = new List<Joint>();

        foreach (var segment in _arm.Segments)
        {
            foreach (var joint in segment.Value.Joints)
            {
                var block = joint.GetBlock();

                if (Util.BlockIsRotor(block) && ((IMyMotorStator)block).RotorLock)
                    lockedJoints.Add(joint);
            }
        }

        return lockedJoints;
    }

    // Locks all joints
    private void LockAllJoints()
    {
        foreach (var segment in _arm.Segments)
        {
            foreach (var joint in segment.Value.Joints)
            {
                var block = joint.GetBlock();

                if (Util.BlockIsRotor(block))
                    ((IMyMotorStator)block).RotorLock = true;
            }
        }
    }
    
    // Unlocks all joints
    private void UnlockAllJoints()
    {
        foreach (var segment in _arm.Segments)
        {
            foreach (var joint in segment.Value.Joints)
            {
                var block = joint.GetBlock();

                if (Util.BlockIsRotor(block))
                    ((IMyMotorStator)block).RotorLock = false;
            }
        }
    }
    
    // Unlocks all joints but the ones that were previously locked manually
    private void UnlockJoints()
    {
        foreach (var segment in _arm.Segments)
        {
            foreach (var joint in segment.Value.Joints)
            {
                var block = joint.GetBlock();

                if (Util.BlockIsRotor(block) && _manuallyLockedJoints.All(j => j != joint))
                    ((IMyMotorStator)block).RotorLock = false;
            }
        }
    }
    
    // Pauses the control of the arm
    public void Pause()
    {
        if(_manuallyPaused) return;
        
        _manuallyPaused = true;

        if (_configuration.PauseLocksJoints)
        {
            _manuallyLockedJoints = GetLockedJoints();
            LockAllJoints();
        }
        
        UpdateDisplays();
    }

    // Unpauses the control of the arm
    public void Unpause()
    {
        _manuallyPaused = false;

        if (_configuration.PauseLocksJoints)
        {
            UnlockJoints();
        }
        
        UpdateDisplays();
    }

    // Call on each update cycle
    public void Update()
    {
        if(_manuallyPaused || _autoPaused && !_restoring) return;
        if (!_autoPaused && !_restoring && _controller.IsUnderControl) _processInput();
        if (!_autoPaused && !_restoring && !_controller.IsUnderControl) _stop();
        if(_restoring) _restore();
    }
    
    // Saves a pose to enable restoring to it
    public void StorePose(string name)
    {
        var config = new MyIni();
        config.TryParse(_controller.CustomData);
        if(!_poses.ContainsKey(name))
            _poses.Add(name, new Dictionary<long, float>());
        
        foreach (var segment in _arm.Segments)
        {
            foreach (var joint in segment.Value.Joints)
            {
                var value = (float) Math.Round(joint.Pose, 2);

                config.Set($"{_arm.Tag}/Poses/{name}", joint.GetBlock().EntityId.ToString(), value);
                _poses[name][joint.GetBlock().EntityId] = value;
            }
        }

        _controller.CustomData = config.ToString();
    }

    // Stops all joints
    private void _stop()
    {
        var joints = new List<Joint>();
        foreach (var segmentsValue in _arm.Segments.Values)
        {
            joints.AddRange(segmentsValue.Joints);
        }

        foreach (var joint in joints)
        {
            joint.Stop();
        }
    }

    // Restores to a specific pose
    public void Go(string pose)
    {
        if (!_poses.ContainsKey(pose)) return;

        _autoPause();
        _restoring = true;
        _restorePose = pose;
        Update();
    }

    public void ToggleLock(bool doLock)
    {
        var mainGroup = Util.GetMainGroup(_configuration.Tag);
        var rotors = new List<IMyMotorStator>();
        
        mainGroup.GetBlocksOfType(rotors);
        foreach (var rotor in rotors)
        {
            rotor.RotorLock = doLock;
        }
    }

    // Redraws any status screens
    public void UpdateDisplays()
    {
        foreach (var screen in _screens)
        {
            var viewport = new RectangleF(
                (screen.TextureSize - screen.SurfaceSize) / 2.0f,
                screen.SurfaceSize);

            screen.ContentType = ContentType.SCRIPT;
            screen.Script = "";

            var spriteList = new Dictionary<int, List<MySprite>>();

            var lineHeight = (screen.SurfaceSize.Y - 10) / 8;
            var textScale = 2.0f;
            var testText = new StringBuilder("O");

            while (screen.MeasureStringInPixels(testText, "Debug", textScale).Y > lineHeight)
            {
                textScale -= 0.1f;
            }

            var groupedJoints = new List<Joint>();

            using (var frame = screen.DrawFrame())
            {
                var position = new Vector2(5, 5) + viewport.Position;

                var titleRectangle = new MySprite
                {
                    Type = SpriteType.TEXTURE,
                    Data = "SquareSimple",
                    Position = new Vector2(viewport.Width / 2, lineHeight / 2) + viewport.Position,
                    Size = new Vector2(viewport.Width, lineHeight),
                    Color = screen.ScriptForegroundColor,
                    Alignment = TextAlignment.CENTER
                };

                var activeSegmentLine = new MySprite
                {
                    Type = SpriteType.TEXT,
                    Data = _activeSegment.Name,
                    Position = position + new Vector2(viewport.Width / 2, -2),
                    RotationOrScale = textScale,
                    Color = screen.ScriptBackgroundColor,
                    Alignment = TextAlignment.CENTER,
                    FontId = "Debug"
                };

                frame.Add(titleRectangle);
                frame.Add(activeSegmentLine);

                foreach (var activeSegmentGroup in _activeSegment.Groups)
                {
                    groupedJoints.AddRange(activeSegmentGroup.Joints);
                    if (activeSegmentGroup.Joints.First().ControlAxis == ControlAxis.None) continue;

                    var controls = _program.ControlMap[activeSegmentGroup.Joints.First().ControlAxis];

                    var instructionSquare1 = new MySprite
                    {
                        Type = SpriteType.TEXTURE,
                        Data = "SquareHollow",
                        Position = new Vector2(lineHeight + 1, lineHeight / 2 + 1),
                        Size = new Vector2(lineHeight * 2, lineHeight - 2),
                        Color = screen.ScriptForegroundColor,
                        Alignment = TextAlignment.CENTER
                    };

                    var instructionKey1 = new MySprite
                    {
                        Type = SpriteType.TEXT,
                        Data = controls[0],
                        Position = new Vector2(lineHeight, 0),
                        RotationOrScale = textScale,
                        Color = screen.ScriptForegroundColor,
                        Alignment = TextAlignment.CENTER,
                        FontId = "Monospace"
                    };

                    var instructionSquare2 = new MySprite
                    {
                        Type = SpriteType.TEXTURE,
                        Data = "SquareHollow",
                        Position = new Vector2(lineHeight * 3 + 2, lineHeight / 2 + 1),
                        Size = new Vector2(lineHeight * 2, lineHeight - 2),
                        Color = screen.ScriptForegroundColor,
                        Alignment = TextAlignment.CENTER
                    };

                    var instructionKey2 = new MySprite
                    {
                        Type = SpriteType.TEXT,
                        Data = controls[1],
                        Position = new Vector2(lineHeight * 3 + 2, 0),
                        RotationOrScale = textScale,
                        Color = screen.ScriptForegroundColor,
                        Alignment = TextAlignment.CENTER,
                        FontId = "Monospace"
                    };

                    var instructionLine = new MySprite
                    {
                        Type = SpriteType.TEXT,
                        Data = activeSegmentGroup.Name.Replace("_", " "),
                        Position = new Vector2(lineHeight * 4 + 8, 0),
                        RotationOrScale = textScale,
                        Color = screen.ScriptForegroundColor,
                        Alignment = TextAlignment.LEFT,
                        FontId = "Debug"
                    };

                    var comps = activeSegmentGroup.Joints;
                    comps.Sort((x, y) => x.Distance.CompareTo(y.Distance));
                    var minDistance = comps.First().Distance;

                    spriteList.Add(minDistance, new List<MySprite>
                    {
                        instructionSquare1,
                        instructionKey1,
                        instructionSquare2,
                        instructionKey2,
                        instructionLine
                    });
                }

                var ungroupedJoints = _activeSegment.Joints.Where(c => !groupedJoints.Contains(c));

                foreach (var joint in ungroupedJoints)
                {
                    if (joint.ControlAxis == ControlAxis.None) continue;
                    if (spriteList.ContainsKey(joint.Distance)) continue;
                    var controls = _program.ControlMap[joint.ControlAxis];

                    var instructionSquare1 = new MySprite
                    {
                        Type = SpriteType.TEXTURE,
                        Data = "SquareHollow",
                        Position = new Vector2(lineHeight + 1, lineHeight / 2 + 1),
                        Size = new Vector2(lineHeight * 2, lineHeight - 2),
                        Color = screen.ScriptForegroundColor,
                        Alignment = TextAlignment.CENTER
                    };
                    
                    var instructionKey1 = new MySprite
                    {
                        Type = SpriteType.TEXT,
                        Data = controls[0],
                        Position = new Vector2(lineHeight, 0),
                        RotationOrScale = textScale,
                        Color = screen.ScriptForegroundColor,
                        Alignment = TextAlignment.CENTER,
                        FontId = "Monospace"
                    };
                    
                    var instructionSquare2 = new MySprite
                    {
                        Type = SpriteType.TEXTURE,
                        Data = "SquareHollow",
                        Position = new Vector2(lineHeight * 3 + 2, lineHeight / 2 + 1),
                        Size = new Vector2(lineHeight * 2, lineHeight - 2),
                        Color = screen.ScriptForegroundColor,
                        Alignment = TextAlignment.CENTER
                    };

                    var instructionKey2 = new MySprite
                    {
                        Type = SpriteType.TEXT,
                        Data = controls[1],
                        Position = new Vector2(lineHeight * 3 + 2, 0),
                        RotationOrScale = textScale,
                        Color = screen.ScriptForegroundColor,
                        Alignment = TextAlignment.CENTER,
                        FontId = "Monospace"
                    };
                    
                    var instructionLine = new MySprite
                    {
                        Type = SpriteType.TEXT,
                        Data = joint.GetBlock().CustomName,
                        Position = new Vector2(lineHeight * 4 + 8, 0),
                        RotationOrScale = textScale,
                        Color = screen.ScriptForegroundColor,
                        Alignment = TextAlignment.LEFT,
                        FontId = "Debug"
                    };

                    spriteList.Add(joint.Distance, new List<MySprite>
                    {
                        instructionSquare1,
                        instructionKey1,
                        instructionSquare2,
                        instructionKey2,
                        instructionLine
                    });
                }

                foreach (var sprites in spriteList.OrderBy(s => s.Key))
                {
                    position += new Vector2(0, lineHeight);

                    foreach (var sprite in sprites.Value)
                    {
                        var spr = sprite;
                        spr.Position += position;
                        frame.Add(spr);
                    }
                }

                position = new Vector2(5, 5) + viewport.Position + new Vector2(0, 7 * lineHeight);
                var controlMode = "Normal mode";

                if (_toolMode) controlMode = "Tool mode";
                if (_manuallyPaused) controlMode = "Paused";
                if (_restoring) controlMode = $"Moving to {_restorePose}";

                var statusBackground = new MySprite
                {
                    Type = SpriteType.TEXTURE,
                    Data = "SquareSimple",
                    Position = position + new Vector2(viewport.Width / 2, lineHeight / 2),
                    Size = new Vector2(viewport.Width, lineHeight),
                    Color = screen.ScriptForegroundColor,
                    Alignment = TextAlignment.CENTER
                };

                var statusLine = new MySprite
                {
                    Type = SpriteType.TEXT,
                    Data = controlMode,
                    Position = position + new Vector2(viewport.Width / 2, 1),
                    RotationOrScale = textScale,
                    Color = screen.ScriptBackgroundColor,
                    Alignment = TextAlignment.CENTER,
                    FontId = "Debug"
                };

                frame.Add(statusBackground);
                frame.Add(statusLine);
            }
        }
    }

    // Builds the arm from a base block. Should only be called once on startup.
    private static void _buildArm(IMyTerminalBlock baseBlock, ref Arm currentArm, int distance)
    {
        var nextJoints = new List<IMyTerminalBlock>();
        var validJoints = currentArm.MainTerminalGroup;

        _program.GridTerminalSystem.GetBlocksOfType(nextJoints, block =>
        {
            IMyCubeGrid nextGrid = null;
            
            if (Util.BlockIsRotor(baseBlock))
                nextGrid = (IMyCubeGrid) ((IMyMotorStator) baseBlock).TopGrid;
            else if (Util.BlockIsPiston(baseBlock))
                nextGrid = (IMyCubeGrid) ((IMyPistonBase) baseBlock).TopGrid;
        
            if (nextGrid == null) return false;

            return block.CubeGrid == nextGrid && validJoints.Contains(block);

        });

        ++distance;
        foreach (var block in nextJoints.Where(
            block => (Util.BlockIsRotor(block) || Util.BlockIsPiston(block)) && validJoints.Contains(block)))
        {
            var joint = new Joint(block, currentArm, distance);
            _buildArm(joint.GetBlock(), ref currentArm, distance);
        }
    }
    
    // Process user input
    private void _processInput()
    {
        foreach (var segment in _arm.Segments.Values)
        {
            if (segment == _arm.ActiveSegment)
            {
                foreach (var joint in segment.Joints)
                {
                    var block = joint.GetBlock();

                    var stator = block as IMyMotorStator;
                    
                    if(stator != null && stator.RotorLock)
                        continue;
                    
                    if (segment.Configuration.UseMouse || (
                            segment.Configuration.UseMouse == false &&
                            Math.Abs(_controller.RotationIndicator.X) == 9 ||
                            _controller.RotationIndicator.X == 0)
                    )
                    {
                        if (joint.ControlAxis == ControlAxis.RotX)
                            joint.Move(
                                _controller.RotationIndicator.X, segment.Configuration.InvertUpDown);
                    }

                    if (segment.Configuration.UseMouse ||
                        segment.Configuration.UseMouse == false &&
                        Math.Abs(_controller.RotationIndicator.Y) == 9 ||
                        _controller.RotationIndicator.Y == 0
                    )
                    {
                        if (joint.ControlAxis == ControlAxis.RotY)
                            joint.Move(
                                _controller.RotationIndicator.Y, segment.Configuration.InvertLeftRight);
                    }

                    if (joint.ControlAxis == ControlAxis.MovX)
                        joint.Move(_controller.MoveIndicator.X, segment.Configuration.InvertAD);
                    if (joint.ControlAxis == ControlAxis.MovY)
                        joint.Move(_controller.MoveIndicator.Y, segment.Configuration.InvertCSpace);
                    if (joint.ControlAxis == ControlAxis.MovZ)
                        joint.Move(_controller.MoveIndicator.Z, segment.Configuration.InvertWS);
                    if (joint.ControlAxis == ControlAxis.Roll)
                        joint.Move(_controller.RollIndicator, segment.Configuration.InvertQE);
                }
            }
            else
            {
                foreach (var joint in segment.Joints)
                {
                    joint.Stop();
                }
            }
        }
    }

    // Internal method to restore to a pose
    private void _restore()
    {
        var joints = new List<Joint>();

        foreach (var segment in _arm.Segments)
        {
            joints.AddRange(segment.Value.Joints);
        }

        joints = joints.Distinct().ToList();
        
        if (_configuration.FastPoseRestore)
        {
            if (joints.All(joint =>
                joint.IsAtPosition(_poses[_restorePose][joint.GetBlock().EntityId])))
            {
                if (_timers.ContainsKey(_restorePose))
                {
                    if(_timers[_restorePose].CustomName.ToLower().Contains("[trigger]"))
                        _timers[_restorePose].Trigger();
                    else
                        _timers[_restorePose].StartCountdown();
                }
                _restoring = false;
                _autoUnpause();
                return;
            }

            foreach (var joint in joints)
            {
                joint.MoveToPosition(_poses[_restorePose][joint.GetBlock().EntityId]);
            }
        }
        else
        {
            var parts = new Dictionary<int, List<Joint>>();

            foreach (var joint in joints)
            {
                var dist = joint.Distance;
                if (joint.Group != null)
                {
                    dist = joint.Group.Joints.OrderBy(c => c.Distance).Last().Distance;
                }
                
                if(!parts.ContainsKey(dist)) parts.Add(dist, new List<Joint>());
                parts[dist].Add(joint);
            }
            
            var minDistance = parts.Keys.OrderBy(p => p).First();
            var distance = minDistance;

            foreach (var part in parts.OrderByDescending(p => p.Key))
            {
                distance = part.Key;
                if (part.Value.Any(joint =>
                    !joint.IsAtPosition(_poses[_restorePose][joint.GetBlock().EntityId])))
                    break;
            }

            if (distance == minDistance && parts[minDistance].All(joint =>
                    joint.IsAtPosition(_poses[_restorePose][joint.GetBlock().EntityId])))
            {
                if (_timers.ContainsKey(_restorePose))
                {
                    if(_timers[_restorePose].CustomName.ToLower().Contains("[trigger]"))
                        _timers[_restorePose].Trigger();
                    else
                        _timers[_restorePose].StartCountdown();
                }
                _restoring = false;
                _autoUnpause();
                return;
            }

            foreach (var joint in parts[distance])
            {
                if (!_poses.ContainsKey(_restorePose) ||
                    !_poses[_restorePose].ContainsKey(joint.GetBlock().EntityId)) continue;

                joint.MoveToPosition(_poses[_restorePose][joint.GetBlock().EntityId]);
            }
        }
    }
    
    // Auto-pauses the control of the arm (e.g. for pose restore)
    private void _autoPause()
    {
        _autoPaused = true;
        UpdateDisplays();
    }
    
    // Unpauses from auto-pause state
    private void _autoUnpause()
    {
        _autoPaused = false;
        UpdateDisplays();
    }
}

// Utility class
private static class Util
{
    // Calculates the closest difference between two angles
    public static float AngleDifference(float destinationAngle, float sourceAngle)
    {
        var distance = (destinationAngle - sourceAngle) % 360;
        if (distance < -180)
            distance += 360;
        else if (distance > 179)
            distance -= 360;

        return distance;
    }

    // Returns a list of valid control axes for direction depending on the type of block queried
    public static List<ControlAxis> AxesForDirection(Base6Directions.Direction direction, IMyTerminalBlock block)
    {
        switch (direction)
        {
            case Base6Directions.Direction.Up:
            case Base6Directions.Direction.Down:
            {
                if (BlockIsRotor(block))  return new List<ControlAxis> {ControlAxis.RotY, ControlAxis.MovX};
                if (BlockIsPiston(block)) return new List<ControlAxis> {ControlAxis.MovY, ControlAxis.MovZ, ControlAxis.RotX};
                break;
            }
            case Base6Directions.Direction.Left:
            case Base6Directions.Direction.Right:
            {
                if (BlockIsRotor(block))  return new List<ControlAxis> {ControlAxis.RotX, ControlAxis.MovZ, ControlAxis.MovY};
                if (BlockIsPiston(block)) return new List<ControlAxis> {ControlAxis.MovX, ControlAxis.RotY, ControlAxis.Roll};
                break;
            }
            case Base6Directions.Direction.Forward:
            case Base6Directions.Direction.Backward:
            {
                if (BlockIsRotor(block))  return new List<ControlAxis> {ControlAxis.Roll};
                if (BlockIsPiston(block)) return new List<ControlAxis> {ControlAxis.MovZ, ControlAxis.RotX, ControlAxis.MovY};
                break;
            }
        }

        return new List<ControlAxis>();
    }

    // Checks if a block is a rotor
    public static bool BlockIsRotor(IMyTerminalBlock block)
    {
        return block is IMyMotorStator;
    }

    // Checks if a block is a piston
    public static bool BlockIsPiston(IMyTerminalBlock block)
    {
        return block is IMyPistonBase;
    }

    // Gets the main terminal group marked with the tag
    public static IMyBlockGroup GetMainGroup(string tag)
    {
        var terminalGroups = new List<IMyBlockGroup>();

        _program.GridTerminalSystem.GetBlockGroups(terminalGroups,
            group => group.Name.Contains($"[{tag}]") && !group.Name.Contains("G:") && !group.Name.Contains("S:"));

        return terminalGroups.FirstOrDefault();
    }

    // Gets any groups marked with the tag and containing a segment name
    public static Dictionary<string, IMyBlockGroup> GetSegmentGroups(string tag)
    {
        var terminalGroups = new List<IMyBlockGroup>();
        var segments = new Dictionary<string, IMyBlockGroup>();
            
        // Define a regular expression for repeated words.
        var rx = new System.Text.RegularExpressions.Regex(@".*S:([\w\d]+).*",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        _program.GridTerminalSystem.GetBlockGroups(terminalGroups,
            group => group.Name.Contains($"[{tag}]") && group.Name.Contains("S:"));

        foreach (var group in terminalGroups)
        {
            var match = rx.Match(group.Name);

            if (match.Groups.Count <= 1) continue;
            
            segments.Add(match.Groups[1].ToString(), group);
        }

        return segments;
    }
    
    // Gets any groups marked with the tag and containing a group name
    public static Dictionary<string, List<IMyTerminalBlock>> GetGroupGroups(string tag)
    {
        var terminalGroups = new List<IMyBlockGroup>();
        var groups = new Dictionary<string, List<IMyTerminalBlock>>();
            
        // Define a regular expression for repeated words.
        var rx = new System.Text.RegularExpressions.Regex(@".*G:([\w\d]+).*",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        _program.GridTerminalSystem.GetBlockGroups(terminalGroups,
            group => group.Name.Contains($"[{tag}]") && group.Name.Contains("G:"));

        foreach (var group in terminalGroups)
        {
            var match = rx.Match(group.Name);

            if (match.Groups.Count <= 1) continue;
            
            var blocks = new List<IMyTerminalBlock>();
            group.GetBlocks(blocks);
                
            groups.Add(match.Groups[1].ToString(), blocks);
        }

        return groups;
    }
}

// Gets the configuration for the whole arm
private GlobalConfiguration GetGlobalConfiguration()
{
    var ini = new MyIni();
    var config = Me.CustomData;
    _program.Storage = config;

    if (config == string.Empty || !ini.TryParse(config))
        return new GlobalConfiguration
        {
            Tag = "Arm",
            UseCockpitScreen = -1,
            FastPoseRestore = false,
            PauseLocksJoints = true
        };
    
    var sections = new List<string>();
    ini.GetSections(sections);

    foreach (var section in sections.Where(section => ini.ContainsKey(section, "Tag") && ini.ContainsKey(section, "UseCockpitScreen")))
    {
        return new GlobalConfiguration
        {
            Tag = ini.Get(section, "Tag").ToString(),
            UseCockpitScreen = ini.Get(section, "UseCockpitScreen").ToInt32(),
            FastPoseRestore = ini.Get(section, "FastPoseRestore").ToBoolean(),
            PauseLocksJoints = ini.Get(section, "PauseLocksJoints").ToBoolean()
        };
    }

    return new GlobalConfiguration
    {
        Tag = "Arm",
        UseCockpitScreen = -1,
        FastPoseRestore = false,
        PauseLocksJoints = true
    };
}

public Program()
{
    _program = this;
    var configuration = GetGlobalConfiguration();
    // Copy custom data to storage for further use
    Storage = Me.CustomData;
    
    var config = new MyIni();

    config.Set("EasyManipulation", "Tag", configuration.Tag);
    config.Set("EasyManipulation", "UseCockpitScreen", configuration.UseCockpitScreen);
    config.Set("EasyManipulation", "FastPoseRestore", configuration.FastPoseRestore);
    config.Set("EasyManipulation", "PauseLocksJoints", configuration.PauseLocksJoints);

    // Get any controllers in the main terminal group
    var shipControllers = new List<IMyShipController>();
    var mainGroup = Util.GetMainGroup(configuration.Tag);

    if (mainGroup == null)
    {
        Echo("Main group not found.\n" +
                $"Please create a group with tag {configuration.Tag}\n" +
                "or change the tag in Custom Data of this block.");
        
        // Save the config
        Me.CustomData = config.ToString();
        Storage = config.ToString();
        
        return;
    }
    
    // Get the ship controllers
    mainGroup.GetBlocksOfType(shipControllers);

    if (shipControllers.Count > 1)
    {
        Echo("More than one ship controller found in the main group.\n" +
                "Please remove extra ship controllers from the group.");
        return;
    }
    if(shipControllers.Count == 0)
    {
        Echo("Could not find any ship controllers in the main group.\n" +
                "Please add a controller to the main group.");
        return;
    }

    if (shipControllers.First().CubeGrid.IsStatic)
    {
        Echo("Designated ship controller is on a static grid\n" +
                "Please move the ship controller to a dynamic grid or subgrid.");
        return;
    }
    
    _controller = new ArmController(configuration, shipControllers.First());
    var arm = _controller.CreateArm();

    foreach (var segment in arm.Segments.Values)
    {
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "RotorSensitivity", segment.Configuration.RotorSensitivity);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "RotorMaxSpeed", segment.Configuration.RotorMaxSpeed);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "RotorMaxOffsetFactor", segment.Configuration.RotorMaxOffsetFactor);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "PistonSensitivity", segment.Configuration.PistonSensitivity);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "PistonMaxSpeed", segment.Configuration.PistonMaxSpeed);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "PistonMaxOffsetFactor", segment.Configuration.PistonMaxOffsetFactor);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "UseMouse", segment.Configuration.UseMouse);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "UseUpDown", segment.Configuration.UseUpDown);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "UseLeftRight", segment.Configuration.UseLeftRight);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "UseWS", segment.Configuration.UseWS);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "UseAD", segment.Configuration.UseAD);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "UseQE", segment.Configuration.UseQE);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "UseCSpace", segment.Configuration.UseCSpace);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "InvertUpDown", segment.Configuration.InvertUpDown);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "InvertLeftRight", segment.Configuration.InvertLeftRight);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "InvertWS", segment.Configuration.InvertWS);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "InvertAD", segment.Configuration.InvertAD);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "InvertQE", segment.Configuration.InvertQE);
        config.Set($"{configuration.Tag}/Segments/{segment.Name}", "InvertCSpace", segment.Configuration.InvertCSpace);
    }

    foreach (var group in arm.Groups.Values)
    {
        config.Set($"{configuration.Tag}/Groups/{group.Name}", "RotorSensitivity", group.Configuration.RotorSensitivity);
        config.Set($"{configuration.Tag}/Groups/{group.Name}", "RotorMaxSpeed", group.Configuration.RotorMaxSpeed);
        config.Set($"{configuration.Tag}/Groups/{group.Name}", "RotorMaxOffsetFactor", group.Configuration.RotorMaxOffsetFactor);
        config.Set($"{configuration.Tag}/Groups/{group.Name}", "PistonSensitivity", group.Configuration.PistonSensitivity);
        config.Set($"{configuration.Tag}/Groups/{group.Name}", "PistonMaxSpeed", group.Configuration.PistonMaxSpeed);
        config.Set($"{configuration.Tag}/Groups/{group.Name}", "PistonMaxOffsetFactor", group.Configuration.PistonMaxOffsetFactor);
        config.Set($"{configuration.Tag}/Groups/{group.Name}", "MirrorGroup", group.Configuration.MirrorGroup);
    }

    // Save the home pose
    _controller.StorePose("Home");

    // Save the config
    Me.CustomData = config.ToString();
    Storage = config.ToString();
    
    // Set the active segment
    _controller.SetActiveSegment("Main");

    // Unpause the control
    _controller.Unpause();

    Runtime.UpdateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update10;
}

public void Main(string argument, UpdateType updateSource)
{
    if ((updateSource & UpdateType.Update1) != 0) 
        _controller.Update();

    if ((updateSource & UpdateType.Update10) != 0)
    {
        _controller.UpdateDisplays();
    }

    if ((updateSource & UpdateType.Trigger) == 0 && (updateSource & UpdateType.Terminal) == 0 &&
        (updateSource & UpdateType.Script) == 0 && (updateSource & UpdateType.IGC) == 0) return;
    
    var command = argument.ToLowerInvariant();
    var parts = argument.Split(' ');
    var commandArgument = parts.Length > 1 ? parts[1] : string.Empty;

    if (command.IndexOf("segment ") == 0 && commandArgument != string.Empty)
        _controller.SetActiveSegment(commandArgument);
    else if (command.IndexOf("store ") == 0 && commandArgument != string.Empty)
        _controller.StorePose(commandArgument);
    else if (command.IndexOf("go ") == 0 && commandArgument != string.Empty)
        _controller.Go(commandArgument);
    else if (command.IndexOf("toolmode ") == 0 && commandArgument != string.Empty)
    {
        if (commandArgument.ToLower() == "on")
            _controller.SetToolMode(true);
        if (commandArgument.ToLower() == "off")
            _controller.SetToolMode(false);
    }
    else if (command == "pause")
        _controller.Pause();
    else if (command == "unpause")
        _controller.Unpause();
    else if (command == "reload")
        _controller.ReloadConfiguration();
    else if (command == "lock")
        _controller.ToggleLock(true);
    else if (command == "unlock")
        _controller.ToggleLock(false);
}
