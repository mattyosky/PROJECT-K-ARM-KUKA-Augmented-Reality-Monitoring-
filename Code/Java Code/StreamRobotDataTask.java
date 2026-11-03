package application;

import com.kuka.device.ForceData; // Added to read the forces
import com.kuka.geometry.Frame;
import com.kuka.math.geometry.Transformation;
import com.kuka.math.geometry.Vector3D; // Added for the force vectors
import com.kuka.roboticsAPI.applicationModel.tasks.CycleBehavior;
import com.kuka.roboticsAPI.applicationModel.tasks.RoboticsAPICyclicBackgroundTask;
import com.kuka.sensitivity.LBR;
import com.kuka.sunrise.common.task.categories.BackgroundTaskCategory;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.util.concurrent.TimeUnit;
import javax.inject.Inject;

@BackgroundTaskCategory(autoStart = true)
public class StreamRobotDataTask extends RoboticsAPICyclicBackgroundTask {
  
  @Inject 
  private LBR robot;
  
  private DatagramSocket socket;
  private InetAddress pcIp;
  private int pcPort = 30001; // Keep port 30001 for your JSON gateway
  
  @Override
  public void initialize() throws Exception {
      try {
          initializeCyclic(0, 20, TimeUnit.MILLISECONDS, CycleBehavior.BEST_EFFORT);
          socket = new DatagramSocket();
          pcIp = InetAddress.getByName("172.31.1.148");
      } catch(Exception e) {
          getLogger().error("Init failed", e);
      }
  }

  @Override
  public void runCyclic() {
      try {
          Frame flange = robot.getFlange();
          
          // 1. Read Cartesian position
          Frame p = robot.getCurrentCartesianPosition(flange);
          Transformation pT = p.getTransformationFromParent();
          
          // 2. Read external forces (from the file you shared with me)
          ForceData fd = robot.getExternalForceTorque(flange);
          Vector3D f = fd.getForce();
      
          // 3. Unified JSON string (Position + Force)
          String msg = String.format(
              java.util.Locale.US,
              "{\"tipo_messaggio\":\"telemetria\", \"posizione_attuale\":{\"x\":%.3f, \"y\":%.3f, \"z\":%.3f}, \"forza\":{\"x\":%.2f, \"y\":%.2f, \"z\":%.2f}}",
              pT.getX(), pT.getY(), pT.getZ(),
              f.getX(), f.getY(), f.getZ()
          );
      
          byte[] data = msg.getBytes("UTF-8");
          DatagramPacket packet = new DatagramPacket(data, data.length, pcIp, pcPort);
          socket.send(packet);
      
      } catch (Exception e) {
          getLogger().error("Streaming failed", e);
      }
  }
}