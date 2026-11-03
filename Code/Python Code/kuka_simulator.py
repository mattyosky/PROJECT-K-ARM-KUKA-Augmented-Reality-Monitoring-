import socket
import json
import time
import math

# =====================================================================
# CONFIGURATION
# =====================================================================
UDP_IP_VISORE = "127.0.0.1" # "172.16.133.245"  # Quest 3 IP address
UDP_PORT = 5005                   # Listening port in Unity

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

def send_json(data):
    msg = json.dumps(data)
    sock.sendto(msg.encode('utf-8'), (UDP_IP_VISORE, UDP_PORT))

def simula_movimento_circolare():
    # Circle points (in the YZ plane of the KUKA space)
    start = {"x": 0.5, "y": -0.3, "z": 0.3}
    mid   = {"x": 0.5, "y": 0.0,  "z": 0.6}
    end   = {"x": 0.5, "y": 0.3,  "z": 0.3}
    
    # 1. Send PREDICTION (3-point arc)
    intent = {
        "tipo_messaggio": "previsione",
        "forma": "circolare",
        "p_partenza": start,
        "p_ausiliario": mid,
        "p_arrivo": end
    }
    send_json(intent)
    print("Inviato Intent Circolare. Il visore dovrebbe disegnare l'arco.")
    time.sleep(1) # Visual pause to observe the path
    
    # 2. Send TELEMETRY (the robot travels along the arc)
    print("Inizio movimento circolare (50Hz)...")
    steps = 150 # 3 seconds at 50Hz
    for i in range(steps + 1):
        t = i / steps
        angle = -math.pi/2 + (t * math.pi) # From -90 to +90 degrees
        
        # Dynamic force generation (inertia and gravity simulation)
        f_x = 2.5 + math.sin(angle * 4) * 1.5 
        f_y = 12.0 * math.cos(angle)
        f_z = -8.5 * math.sin(angle)

        telemetria = {
            "tipo_messaggio": "telemetria",
            "posizione_attuale": {
                "x": 0.5,
                "y": 0.3 * math.sin(angle), # Center Y=0, radius=0.3
                "z": 0.3 + 0.3 * math.cos(angle) # Center Z=0.3, radius=0.3
            },
            "forza": {
                "x": f_x,
                "y": f_y,
                "z": f_z
            }
        }
        send_json(telemetria)
        time.sleep(0.02)

def simula_movimento_lineare():
    # Points to return to the start along a straight line
    start = {"x": 0.5, "y": 0.3,  "z": 0.3}
    end   = {"x": 0.5, "y": -0.3, "z": 0.3}
    
    # 1. Send PREDICTION (line)
    intent = {
        "tipo_messaggio": "previsione",
        "forma": "lineare",
        "p_partenza": start,
        "p_arrivo": end
    }
    send_json(intent)
    print("Inviato Intent Lineare. Il visore dovrebbe disegnare la linea di ritorno.")
    time.sleep(1)
    
    # 2. Send TELEMETRY (the robot travels along the line)
    print("Inizio movimento lineare (50Hz)...")
    steps = 100 # 2 seconds at 50Hz
    for i in range(steps + 1):
        t = i / steps
        
        # Force generation (progressive resistance simulation)
        f_x = 3.0 + (t * 5.0)  # Increases linearly from 3.0 to 8.0 N
        f_y = -4.2             # Constant
        f_z = 15.0 - (t * 7.0) # Decreases linearly from 15.0 to 8.0 N

        telemetria = {
            "tipo_messaggio": "telemetria",
            "posizione_attuale": {
                "x": start["x"] + t * (end["x"] - start["x"]),
                "y": start["y"] + t * (end["y"] - start["y"]),
                "z": start["z"] + t * (end["z"] - start["z"])
            },
            "forza": {
                "x": f_x,
                "y": f_y,
                "z": f_z
            }
        }
        send_json(telemetria)
        time.sleep(0.02)

# =====================================================================
# MAIN LOOP
# =====================================================================
try:
    print(f"Avvio simulatore AR verso Quest 3 (IP: {UDP_IP_VISORE}:{UDP_PORT})...")
    print("Assicurati di aver disabilitato la 'modalitaSimulazione' nell'Inspector di Unity.")
    print("-" * 50)
    
    while True:
        simula_movimento_circolare()
        time.sleep(1.5)
        simula_movimento_lineare()
        time.sleep(1.5)
        
except KeyboardInterrupt:
    print("\nSimulazione terminata manualmente.")