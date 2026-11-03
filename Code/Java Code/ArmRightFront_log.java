package application;

import java.util.concurrent.TimeUnit;
import javax.inject.Inject;
import static com.kuka.roboticsAPI.motionModel.BasicMotions.ptp;

import com.kuka.device.common.JointPosition;
import com.kuka.geometry.LocalFrame;
import com.kuka.geometry.ObjectFrame;
import com.kuka.geometry.SpatialObject;
import com.kuka.geometry.Tool;
import com.kuka.geometry.Frame; // NEW IMPORT
import com.kuka.math.geometry.Transformation;
import com.kuka.med.devicemodel.LBRMed;
import com.kuka.roboticsAPI.applicationModel.RoboticsAPIApplication;
import com.kuka.roboticsAPI.motionModel.controlModeModel.CartDOF;
import com.kuka.roboticsAPI.sensorModel.IDataRecorder;
import com.kuka.roboticsAPI.sensorModel.RecorderConfiguration;
import com.kuka.sensitivity.controlmode.CartesianImpedanceControlMode;
import com.kuka.servoing.api.common.EServoRequestState;
import com.kuka.servoing.api.common.IServoingCapability;
import com.kuka.servoing.api.smartservolin.ISmartServoLin;
import com.kuka.servoing.api.smartservolin.ISmartServoLinRuntime;
import com.kuka.task.ITaskLogger;
import com.kuka.threading.ThreadUtil;

// NEW IMPORTS FOR UDP NETWORKING
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;

public class ArmRightFront_log extends RoboticsAPIApplication {

    @Inject
    private LBRMed robot;

    @Inject
    private IServoingCapability servoCapability;
    
    @Inject
    private IDataRecorder recorder;

    private ITaskLogger logger;
    private ISmartServoLinRuntime servoRuntime;
    private Tool tool;

    // ---------------------------------------------------------------------
    // Network variables (NEW)
    // ---------------------------------------------------------------------
    private DatagramSocket socket;
    private InetAddress pcIp;
    private int pcPort = 30001;

    // ---------------------------------------------------------------------
    // Tool configuration
    // ---------------------------------------------------------------------
    private static final String TOOL_OBJECT_NAME = "ArmTool";
    private static final String TOOL_TCP_FRAME_PATH = "";

    // ---------------------------------------------------------------------
    // Arc and repetition parameters
    // ---------------------------------------------------------------------
    private static final double RADIUS_MM = 400.0;
    private static final double ARC_ANGLE_DEG = 45.0;
    private static final double ARC_DURATION_S = 3.0;
    private static final int ITERATION_COUNT = 5;
    private static final long ENDPOINT_DWELL_MS = 250L;
    private static final long COMMAND_PERIOD_MS = 20L;
    private static final double MIN_TRAJECTORY_TIME_S = 0.040;

    private static final double MAX_TRANSLATION_VELOCITY_MM_S = 170;
    private static final double MAX_TRANSLATION_ACCELERATION_MM_S2 = 200;
    private static final double MAX_ORIENTATION_VELOCITY_RAD_S = 0.5;
    private static final double MAX_ORIENTATION_ACCELERATION_RAD_S2 = 0.5;

    private static final long FINAL_DESTINATION_TIMEOUT_MS = 3000L;

    // ---------------------------------------------------------------------
    // Cartesian impedance parameters
    // ---------------------------------------------------------------------
    private static final double TANGENTIAL_STIFFNESS_N_M = 150.0;
    private static final double TRANSVERSE_STIFFNESS_N_M = 3000.0;
    private static final double ROTATIONAL_STIFFNESS_NM_RAD = 200.0;
    private static final double CARTESIAN_DAMPING = 1.0;
    private static final double NULL_SPACE_STIFFNESS_NM_RAD = 100.0;
    private static final double NULL_SPACE_DAMPING = 0.7;
    private static final double MAX_DEVIATION_TANGENT_MM = 500.0;
    private static final double MAX_DEVIATION_TRANSVERSE_MM = 50.0;
    private static final double MAX_DEVIATION_ROTATION_RAD = 1.0;
    private static final double MAX_FORCE_TANGENT_N = 30.0;
    private static final double MAX_FORCE_TRANSVERSE_N = 60.0;
    private static final double MAX_TORQUE_NM = 15.0;

    @Override
    public void initialize() throws Exception {
        logger = getLogger();

        // UDP SOCKET INITIALIZATION (NEW)
        try {
            socket = new DatagramSocket();
            pcIp = InetAddress.getByName("172.31.1.148"); 
        } catch(Exception e) {
            logger.error("Errore inizializzazione socket UDP Intent", e);
        }

        final SpatialObject sceneObject = robot.findObject(TOOL_OBJECT_NAME);
        if (sceneObject == null) {
            throw new IllegalStateException("Tool object not found");
        }
        if (!(sceneObject instanceof Tool)) {
            throw new IllegalStateException("Object is not a Tool");
        }
        tool = (Tool) sceneObject;
    }

    @Override
    public void run() throws Exception {
        logger = getLogger();
        validateParameters();
        
        ObjectFrame worldFrame = robot.getRootFrame();

        RecorderConfiguration config = recorder.getConfigurator()
            .recordCurrentCartesianPositionXYZ(robot.getFlange(), worldFrame)
            .recordCartesianTorque(robot.getFlange(), worldFrame)
            .recordCartesianForce(robot.getFlange(), worldFrame)
            .setSampleInterval(10)
            .setTimeout(120, TimeUnit.SECONDS)
            .setFileName("flange_pos_force_ArmRightFront.log")
            .buildConfiguration();

        recorder.enable(config);
        recorder.startRecording(config);

        final ObjectFrame motionFrame = resolveToolMotionFrame();
        logger.info("Moving to start joint position under position control.");

        final JointPosition target = JointPosition.ofDeg(
           -127.0,  // A1
             85.0,  // A2
             89.0,  // A3
            -89.0,  // A4
              5.5,  // A5
            -34.0,  // A6
            -90.0   // A7
        );
        
        motionFrame.move(ptp(target).setJointVelocityRel(0.15));
        ThreadUtil.milliSleep(3000L);

        final LocalFrame startPose = robot.getCurrentCartesianPosition(motionFrame);
        final Transformation startPoseTransformation = startPose.getTransformationFromParent();

        final double circleStartAngleRad = Math.PI;
        final double circleEndAngleRad = Math.toRadians(135.00);
        final double centerX = startPoseTransformation.getX() - RADIUS_MM * Math.cos(circleStartAngleRad);
        final double centerY = startPoseTransformation.getY();
        final double centerZ = startPoseTransformation.getZ() - RADIUS_MM * Math.sin(circleStartAngleRad);

        final ISmartServoLin servoMotion = servoCapability.createSmartServoLinMotion(startPose);
        configureServoMotionLimits(servoMotion);

        final CartesianImpedanceControlMode impedanceMode = createImpedanceMode();

        logger.info("Validating the load model");
        servoMotion.validateForImpedanceMode(tool);

        motionFrame.moveAsync(servoMotion.setMode(impedanceMode));

        servoRuntime = servoMotion.getRuntime(1L, TimeUnit.SECONDS);
        servoRuntime.setMinimumTrajectoryExecutionTime(MIN_TRAJECTORY_TIME_S);

        boolean sequenceCompleted = true;

        try {
            for (int iteration = 1; iteration <= ITERATION_COUNT; iteration++) {
                logger.info("Iteration " + iteration + "/" + ITERATION_COUNT + ": forward arc.");

                if (!executeArcLeg(
                        startPose, startPoseTransformation, centerX, centerY, centerZ,
                        circleStartAngleRad, circleStartAngleRad, circleEndAngleRad)) {
                    sequenceCompleted = false; break;
                }

                dwellAtEndpoint();

                logger.info("Iteration " + iteration + "/" + ITERATION_COUNT + ": return arc.");

                if (!executeArcLeg(
                        startPose, startPoseTransformation, centerX, centerY, centerZ,
                        circleStartAngleRad, circleEndAngleRad, circleStartAngleRad)) {
                    sequenceCompleted = false; break;
                }

                if (iteration < ITERATION_COUNT) {
                    dwellAtEndpoint();
                }
            }
        } finally {
            if (servoRuntime != null) {
                servoRuntime.stopMotion();
            }
            recorder.stopRecording();
        }
    }

    private boolean executeArcLeg(
            final LocalFrame startPose,
            final Transformation startPoseTransformation,
            final double centerX,
            final double centerY,
            final double centerZ,
            final double circleStartAngleRad,
            final double legStartAngleRad,
            final double legEndAngleRad) {

        // --- NEW: compute and send the Intent to Unity BEFORE the motion ---
        double midAngleRad = (legStartAngleRad + legEndAngleRad) / 2.0;
        
        LocalFrame intentStart = createArcDestination(startPose, startPoseTransformation, centerX, centerY, centerZ, circleStartAngleRad, legStartAngleRad);
        LocalFrame intentMid = createArcDestination(startPose, startPoseTransformation, centerX, centerY, centerZ, circleStartAngleRad, midAngleRad);
        LocalFrame intentEnd = createArcDestination(startPose, startPoseTransformation, centerX, centerY, centerZ, circleStartAngleRad, legEndAngleRad);
        
        sendCircularIntent(intentStart, intentMid, intentEnd);
        // -----------------------------------------------------------------------

        final long commandPeriodNs = TimeUnit.MILLISECONDS.toNanos(COMMAND_PERIOD_MS);
        final long legStartTimeNs = System.nanoTime();
        long nextCommandTimeNs = legStartTimeNs;

        while (true) {
            final long nowNs = System.nanoTime();
            final double elapsedTimeS = (nowNs - legStartTimeNs) * 1.0e-9;
            final double normalizedTime = Math.min(elapsedTimeS / ARC_DURATION_S, 1.0);
            final double smoothProgress = 0.5 - 0.5 * Math.cos(Math.PI * normalizedTime);
            
            final double angleRad = legStartAngleRad + (legEndAngleRad - legStartAngleRad) * smoothProgress;

            final LocalFrame destination = createArcDestination(
                startPose, startPoseTransformation, centerX, centerY, centerZ, circleStartAngleRad, angleRad
            );

            final EServoRequestState requestState = servoRuntime.setDestination(destination);

            if (requestState != EServoRequestState.SUCCESSFUL) {
                logger.warn("setDestination failed: " + requestState);
                return false;
            }

            if (normalizedTime >= 1.0) break;

            nextCommandTimeNs += commandPeriodNs;
            if (nextCommandTimeNs < System.nanoTime()) nextCommandTimeNs = System.nanoTime();
            sleepUntil(nextCommandTimeNs);
        }

        final LocalFrame exactEndpoint = createArcDestination(
            startPose, startPoseTransformation, centerX, centerY, centerZ, circleStartAngleRad, legEndAngleRad
        );

        final EServoRequestState finalState = servoRuntime.setDestination(exactEndpoint);
        if (finalState != EServoRequestState.SUCCESSFUL) return false;

        return waitUntilDestinationReached(FINAL_DESTINATION_TIMEOUT_MS);
    }

    // --- NEW: method that transmits the trajectory JSON UDP packet ---
    private void sendCircularIntent(Frame startFrame, Frame auxFrame, Frame endFrame) {
        try {
            if (socket == null || pcIp == null) return;
            
            Transformation startT = startFrame.getTransformationFromParent();
            Transformation auxT = auxFrame.getTransformationFromParent();
            Transformation endT = endFrame.getTransformationFromParent();
            
            String msg = String.format(
                java.util.Locale.US,
                "{\"tipo_messaggio\":\"previsione\", \"forma\":\"circolare\", \"p_partenza\":{\"x\":%.3f, \"y\":%.3f, \"z\":%.3f}, \"p_ausiliario\":{\"x\":%.3f, \"y\":%.3f, \"z\":%.3f}, \"p_arrivo\":{\"x\":%.3f, \"y\":%.3f, \"z\":%.3f}}",
                startT.getX(), startT.getY(), startT.getZ(),
                auxT.getX(), auxT.getY(), auxT.getZ(),
                endT.getX(), endT.getY(), endT.getZ()
            );
            
            byte[] data = msg.getBytes("UTF-8");
            socket.send(new DatagramPacket(data, data.length, pcIp, pcPort));
        } catch (Exception e) { 
            logger.error("Errore intent circolare", e); 
        }
    }
    // -----------------------------------------------------------------------------

    private LocalFrame createArcDestination(
            final LocalFrame startPose,
            final Transformation startPoseTransformation,
            final double centerX,
            final double centerY,
            final double centerZ,
            final double circleStartAngleRad,
            final double angleRad) {

        final double destinationX = centerX + RADIUS_MM * Math.cos(angleRad);
        final double destinationY = centerY;
        final double destinationZ = centerZ + RADIUS_MM * Math.sin(angleRad);
        final double relativeArcAngleRad = angleRad - circleStartAngleRad;

        final Transformation baseYRotation = Transformation.ofRad(0.0, 0.0, 0.0, 0.0, -relativeArcAngleRad, 0.0);
        final Transformation destinationTransformation = baseYRotation.compose(startPoseTransformation).withTranslation(destinationX, destinationY, destinationZ);

        final LocalFrame destination = LocalFrame.copyOfWithRedundancy(startPose, startPose.getParent());
        destination.setTransformationFromParent(destinationTransformation);

        return destination;
    }

    private void dwellAtEndpoint() {
        if (ENDPOINT_DWELL_MS > 0L) ThreadUtil.milliSleep(ENDPOINT_DWELL_MS);
    }

    private void sleepUntil(final long deadlineNs) {
        final long remainingNs = deadlineNs - System.nanoTime();
        if (remainingNs <= 0L) return;
        final long remainingMs = (remainingNs + 999_999L) / 1_000_000L;
        ThreadUtil.milliSleep(remainingMs);
    }

    private boolean waitUntilDestinationReached(final long timeoutMs) {
        final long deadlineNs = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs);
        while (System.nanoTime() < deadlineNs) {
            final EServoRequestState updateState = servoRuntime.updateWithRealtimeSystem();
            if (updateState != EServoRequestState.SUCCESSFUL) return false;
            if (servoRuntime.isDestinationReached()) return true;
            ThreadUtil.milliSleep(10L);
        }
        return false;
    }

    private ObjectFrame resolveToolMotionFrame() {
        final ObjectFrame motionFrame;
        if (TOOL_TCP_FRAME_PATH == null || TOOL_TCP_FRAME_PATH.trim().isEmpty()) {
            motionFrame = tool.getDefaultMotionFrame();
        } else {
            motionFrame = tool.findFrame(TOOL_TCP_FRAME_PATH);
        }
        if (motionFrame == null) throw new IllegalStateException("Unable to resolve the tool motion frame.");
        return motionFrame;
    }

    private void configureServoMotionLimits(final ISmartServoLin servoMotion) {
        servoMotion.setMaxTranslationVelocity(new double[] { MAX_TRANSLATION_VELOCITY_MM_S, MAX_TRANSLATION_VELOCITY_MM_S, MAX_TRANSLATION_VELOCITY_MM_S });
        servoMotion.setMaxTranslationAcceleration(new double[] { MAX_TRANSLATION_ACCELERATION_MM_S2, MAX_TRANSLATION_ACCELERATION_MM_S2, MAX_TRANSLATION_ACCELERATION_MM_S2 });
        servoMotion.setMaxRotationVelocity(new double[] { MAX_ORIENTATION_VELOCITY_RAD_S, MAX_ORIENTATION_VELOCITY_RAD_S, MAX_ORIENTATION_VELOCITY_RAD_S });
        servoMotion.setMaxRotationAcceleration(new double[] { MAX_ORIENTATION_ACCELERATION_RAD_S2, MAX_ORIENTATION_ACCELERATION_RAD_S2, MAX_ORIENTATION_ACCELERATION_RAD_S2 });
    }

    private CartesianImpedanceControlMode createImpedanceMode() {
        final CartesianImpedanceControlMode mode = new CartesianImpedanceControlMode();
        mode.parametrize(CartDOF.X).setStiffness(TANGENTIAL_STIFFNESS_N_M);
        mode.parametrize(CartDOF.Y, CartDOF.Z).setStiffness(TRANSVERSE_STIFFNESS_N_M);
        mode.parametrize(CartDOF.ROT).setStiffness(ROTATIONAL_STIFFNESS_NM_RAD);
        mode.parametrize(CartDOF.ALL).setDamping(CARTESIAN_DAMPING);
        mode.setNullSpaceStiffness(NULL_SPACE_STIFFNESS_NM_RAD);
        mode.setNullSpaceDamping(NULL_SPACE_DAMPING);
        mode.setMaxPathDeviation(MAX_DEVIATION_TANGENT_MM, MAX_DEVIATION_TRANSVERSE_MM, MAX_DEVIATION_TRANSVERSE_MM, MAX_DEVIATION_ROTATION_RAD, MAX_DEVIATION_ROTATION_RAD, MAX_DEVIATION_ROTATION_RAD);
        mode.setMaxControlForce(MAX_FORCE_TANGENT_N, MAX_FORCE_TRANSVERSE_N, MAX_FORCE_TRANSVERSE_N, MAX_TORQUE_NM, MAX_TORQUE_NM, MAX_TORQUE_NM, true);
        return mode;
    }

    private void validateParameters() {
        if (RADIUS_MM <= 0.0) throw new IllegalArgumentException("RADIUS_MM must be greater than zero.");
        if (ARC_ANGLE_DEG == 0.0) throw new IllegalArgumentException("ARC_ANGLE_DEG must not be zero.");
        if (ARC_DURATION_S <= 0.0) throw new IllegalArgumentException("ARC_DURATION_S must be greater than zero.");
        if (ITERATION_COUNT <= 0) throw new IllegalArgumentException("ITERATION_COUNT must be greater than zero.");
    }
}