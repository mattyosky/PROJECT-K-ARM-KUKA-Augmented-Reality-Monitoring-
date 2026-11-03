import socket
import json

UDP_IP_RECEIVE = "0.0.0.0" 
UDP_PORT_RECEIVE = 30001 # PORT ON WHICH DATA FROM THE JAVA SCRIPT ARRIVES

sock_receive = socket.socket(socket.AF_INET, socket.SOCK_DGRAM) # define the socket used to receive data
sock_receive.bind((UDP_IP_RECEIVE, UDP_PORT_RECEIVE)) # bind the socket to the receive port

UDP_IP_UNITY = "10.76.181.245" # since Unity runs on the same PC as the script
UDP_PORT_UNITY = 5005  # use the same port configured in Unity

sock_send = socket.socket(socket.AF_INET, socket.SOCK_DGRAM) # define the socket used to send data to Unity

print("Gateway KUKA (JSON) avviato.") 
print(f"In ascolto dal robot sulla porta {UDP_PORT_RECEIVE}...") 
print(f"Inoltro dati a Unity su {UDP_IP_UNITY}:{UDP_PORT_UNITY}...") 
print("-" * 50) 

# Helper function to convert points from mm (KUKA) to meters (Unity)
def converti_in_metri(punto_mm):
    return {
        "x": float(punto_mm["x"]) / 1000.0,
        "y": float(punto_mm["y"]) / 1000.0,
        "z": float(punto_mm["z"]) / 1000.0
    }

while True:
    try:
        # receive packets from the KUKA
        data, addr = sock_receive.recvfrom(4096) 
        testo = data.decode("utf-8").strip() 
        
        # Convert the JSON string into a Python dictionary
        pacchetto = json.loads(testo)
        
        # 1. Case: continuous TELEMETRY received
        if pacchetto.get("tipo_messaggio") == "telemetria":
            pacchetto["posizione_attuale"] = converti_in_metri(pacchetto["posizione_attuale"])
            
        # 2. Case: trajectory INTENT received
        elif pacchetto.get("tipo_messaggio") == "previsione":
            if "p_partenza" in pacchetto:
                pacchetto["p_partenza"] = converti_in_metri(pacchetto["p_partenza"])
            if "p_arrivo" in pacchetto:
                pacchetto["p_arrivo"] = converti_in_metri(pacchetto["p_arrivo"])
            if "p_ausiliario" in pacchetto:
                pacchetto["p_ausiliario"] = converti_in_metri(pacchetto["p_ausiliario"])
        
        # Convert the dictionary (now in meters) back into a JSON string
        messaggio_unity = json.dumps(pacchetto)
        
        # Send the clean JSON to Unity
        sock_send.sendto(messaggio_unity.encode("utf-8"), (UDP_IP_UNITY, UDP_PORT_UNITY))

    except json.JSONDecodeError:
        print("Ricevuto un pacchetto non in formato JSON, ignorato.")
    except Exception as e:
        print(f"Errore di ricezione/inoltro: {e}")