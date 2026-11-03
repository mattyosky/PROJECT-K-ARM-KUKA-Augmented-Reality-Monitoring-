# Spatial Augmented Reality Digital Twin for a KUKA LBR Med Collaborative Robot

> **Bachelor's Degree Thesis Project**  
> *Development of an Augmented Reality Environment for Data Extraction from a Robotic Device for Medical Rehabilitation*

This repository contains the complete software architecture for an end-to-end **Spatial Augmented Reality (AR) Digital Twin** deployed on **Meta Quest 3** using **Unity 3D**. The platform extracts, normalizes, and visualizes real-time kinematic and interaction force telemetry from a **KUKA LBR Med** collaborative manipulator used in medical rehabilitation workflows.

---

## Table of Contents

1. [System Overview & Architecture](#1-system-overview--architecture)
2. [Spatial Registration & Mathematical Model](#2-spatial-registration--mathematical-model)
3. [Unity Scene Hierarchy](#3-unity-scene-hierarchy)
4. [Repository Structure](#4-repository-structure)
5. [Prerequisites & Network Configuration](#5-prerequisites--network-configuration)
6. [How to Run the System (Step-by-Step)](#6-how-to-run-the-system-step-by-step)
7. [Meta Quest 3 Setup & Deployment Guide](#7-meta-quest-3-setup--deployment-guide)

---

## 1. System Overview & Architecture

The system is built around a three-tier distributed UDP pipeline designed to satisfy soft real-time constraints, keeping end-to-end telemetry latency within $15\text{--}20\text{ ms}$ at a $50\text{ Hz}$ update rate.

```text
+-----------------------+          UDP           +-----------------------+          UDP           +-----------------------+
|     KUKA LBR Med      |      (Port 30001)      |      Gateway PC       |      (Port 5005)       |     Meta Quest 3      |
|   Sunrise OS (Java)   | ---------------------> |     Python Script     | ---------------------> |     Unity 3D (C#)     |
|      Controller       |                        |     Normalization     |                        |   Trajectory, Hand    |
|  Kinematics & Forces  |                        |     & Forwarding      |                        |     Tracking, HMI     |
+-----------------------+                        +-----------------------+                        +-----------------------+
```

### Tier 1 — KUKA LBR Med Controller (`Sunrise OS / Java`)
* Interfaces directly with the robot controller to sample real-time joint positions, Cartesian end-effector pose, and external force/torque vectors acting on the manipulator.
* Packages the state payload and streams datagrams via **UDP on Port `30001`**.

### Tier 2 — Gateway PC (`Python Middleware`)
* Acts as an intermediate bridge between the isolated robotic subnetwork and the wireless local network hosting the headset.
* Listens on **UDP Port `30001`**, parses and normalizes incoming telemetry (converting millimeters to meters and filtering packet jitter), and forwards lightweight JSON/binary packets via **UDP on Port `5005`** to the Meta Quest 3 IP address.

### Tier 3 — Meta Quest 3 (`Unity 3D / C#`)
* Listens asynchronously on **UDP Port `5005`** and updates the holographic twin in real time.
* Renders predictive end-effector trajectories, real-time force vectors, and billboard HUDs oriented toward the user's head during `LateUpdate`.
* Provides a controller-free **Human-Machine Interface (HMI)** driven entirely by native Meta Quest 3 **Hand Tracking**.

---

## 2. Spatial Registration & Mathematical Model

Aligning the virtual digital twin with the physical manipulator in Mixed Reality requires solving two distinct geometric transformations: **chirality conversion** between coordinate systems and **6-DoF room-scale anchoring**.

### 2.1 Coordinate Frame Chirality Conversion ($\det(\mathbf{S}) = -1$)

The KUKA Robot Base Coordinate System and the Unity local space exhibit opposite chirality:
* **KUKA Frame**: Right-handed coordinate system, vertical axis $Z_K$, coordinates expressed in millimeters ($\text{mm}$).
* **Unity Frame**: Left-handed coordinate system, vertical axis $Y_U$, coordinates expressed in meters ($\text{m}$).

Because the two frames are mirror images of each other, no proper 3D rotation matrix ($\det(\mathbf{R}) = +1$) can superimpose them. Every point transmitted by the robot is therefore mapped into the local space of Unity's `RobotRoot` via an **improper reflection matrix** $\mathbf{S}$ with determinant equal to $-1$:

$$
\begin{bmatrix} x_U \\ y_U \\ z_U \end{bmatrix} = \mathbf{S} \begin{bmatrix} x_K \\ y_K \\ z_K \end{bmatrix} = \begin{bmatrix} -1 & 0 & 0 \\ 0 & 0 & 1 \\ 0 & -1 & 0 \end{bmatrix} \begin{bmatrix} x_K \\ y_K \\ z_K \end{bmatrix}, \qquad \det(\mathbf{S}) = -1
$$

### 2.2 Optical Marker Calibration via Meta MRUK

To localize the physical robot base inside the room at the beginning of each session:
1. A physical **QR Code marker** is rigidly mounted at a known mechanical offset relative to the KUKA manipulator base.
2. Using the **Meta Mixed Reality Utility Kit (MRUK)** (`MarkerManager.cs`), the headset cameras detect the QR code and estimate its 6-DoF pose in Unity world coordinates.
3. The `RobotRoot` transform is anchored to the detected marker pose, while the child object `Offset_Meccanico` compensates for the physical translation and rotation between the QR sticker and the robot base origin.

---

## 3. Unity Scene Hierarchy

The spatial transformation chain is directly reflected in the Unity hierarchy under `RobotRoot`:

```text
▼ RobotRoot                  <-- Anchored dynamically to the QR Code pose via MRUK
  ▶ TABLE                    <-- Virtual reference geometry of the support table
  ▼ Offset_Meccanico         <-- Rigid calibration offset from QR marker to KUKA base
      robot_hand_target      <-- Target transform updated via UDP + matrix S
      Trajectory_manager     <-- Renders predictive and historical 3D paths
      Robot                  <-- Articulated 3D mesh of the KUKA LBR Med
    Sphere                   <-- Visual debug anchor at the origin
```

---

## 4. Repository Structure

```text
├── KUKA_Sunrise/            # Java application for KUKA Sunrise.OS controller (UDP Port 30001)
├── Python_Gateway/          # Python middleware for telemetry normalization & forwarding (Port 30001 -> 5005)
├── Unity_Quest3/            # Unity 3D project (C#, Meta XR SDK, MRUK, Hand Tracking HMI)
│   ├── Assets/
│   ├── Packages/
│   └── ProjectSettings/
├── docs/                    # Architecture diagrams, QR marker printable template, and guides
│   └── README_QUEST3_EN.md  # Detailed Meta Quest 3 Developer & ADB Setup Guide
├── .gitignore               # Git filter rules for Unity, Python, and Java
└── README.md                # Main system documentation (this file)
```

---

## 5. Prerequisites & Network Configuration

### Hardware & Software Requirements
* **Robot**: KUKA LBR Med (Sunrise.OS controller).
* **Gateway PC**: Windows/Linux PC with Python $3.8+$ installed.
* **Headset**: Meta Quest 3 with Developer Mode enabled and firmware supporting MRUK QR tracking.
* **Development Environment**: Unity $6\text{ LTS}$ (or $2022.3\text{ LTS}$) with **Meta XR All-in-One SDK**.

### Network Setup
1. Connect the **Gateway PC** to the KUKA controller network interface (Ethernet) and to the local Wi-Fi router (5 GHz / 6 GHz recommended).
2. Connect the **Meta Quest 3** to the same Wi-Fi network as the Gateway PC.
3. Verify the IP addresses:
   * In the **KUKA Java application**, set the destination IP to the Gateway PC's Ethernet IP address and port to `30001`.
   * In the **Python Gateway script**, set `LISTEN_PORT = 30001`, `TARGET_PORT = 5005`, and `QUEST_IP` to the local Wi-Fi IP address of the Meta Quest 3 (or broadcast address).
   * Ensure Windows Firewall on the Gateway PC allows inbound/outbound UDP traffic on ports `30001` and `5005`.

---

## 6. How to Run the System (Step-by-Step)

Follow this exact sequence to start a full telemetry and Augmented Reality session:

### Step 1: Place the Calibration QR Code
Ensure the physical QR Code marker is positioned in its calibrated slot on the robot table and is well lit.

### Step 2: Start the Python UDP Gateway (PC)
Open a terminal on the Gateway PC, navigate to the `Python_Gateway/` folder, and launch the forwarding script:

```bash
cd Python_Gateway
python udp_gateway.py
```
The console will confirm that it is listening on UDP port `30001` and ready to forward packets to port `5005`.

### Step 3: Start the KUKA LBR Med Application (SmartPAD)
1. On the KUKA SmartPAD, select the telemetry application developed in Sunrise Workbench.
2. Run the application in `T1` or `AUT` mode depending on the rehabilitation protocol.
3. Verify on the Gateway PC terminal that UDP packets are being received at the nominal frequency.

### Step 4: Launch the Meta Quest 3 App & Calibrate
1. Put on the Meta Quest 3 and launch the standalone application from **App Library $\rightarrow$ Unknown Sources** (or press **Play** in the Unity Editor via Meta Quest Link).
2. Look directly at the physical **QR Code** attached to the robot cell from a distance of $40\text{--}80\text{ cm}$.
3. Once `MarkerManager` locks onto the QR code, the holographic KUKA LBR Med and trajectory overlays will snap into 1:1 alignment with the physical manipulator.
4. Use **Hand Tracking** (pinch gestures and direct finger pokes) to interact with the spatial HMI menus, toggle force/torque telemetry HUDs, and inspect trajectories in real time.

---

## 7. Meta Quest 3 Setup & Deployment Guide

For detailed instructions on how to:
* Enable **Developer Mode** on Meta Quest 3 and avoid secondary-account pitfalls;
* Configure **Insight Passthrough** and **MRUK** in Unity;
* Run live tests via **Meta Quest Link** without physical controllers;
* Build the Android `.apk` and sideload it rapidly via command-line **ADB** (`adb install -r`);

Please refer to the dedicated operational guide:  
👉 **[Meta Quest 3 Development & Deployment Guide (`README_EN.md`)](./README_EN.md)**

---

## License & Citation

Developed as a Bachelor's Degree Thesis project in Engineering.  
If you use this architecture or codebase as a reference for academic or research purposes, please cite the corresponding thesis work:

> *Development of an Augmented Reality Environment for Data Extraction from a Robotic Device for Medical Rehabilitation*, Bachelor's Degree Thesis, 2026.