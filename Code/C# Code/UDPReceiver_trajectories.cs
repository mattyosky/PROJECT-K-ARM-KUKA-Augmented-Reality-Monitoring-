using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Collections;
using TMPro;

// ============================================================================
// 1. JSON DATA STRUCTURES
// ============================================================================

[Serializable]
public class JsonPoint {
    public float x;
    public float y;
    public float z;

    public Vector3 ToVector3()
    {
        return CoordinateConverter.ConvertKukaToUnity(x, y, z);
    }
}

[Serializable]
public class KukaMessage {
    public string tipo_messaggio;
    public string forma;
    public JsonPoint posizione_attuale;
    public JsonPoint forza; // NEW FIELD: extraction of forces in Newtons
    public JsonPoint p_partenza;
    public JsonPoint p_arrivo;
    public JsonPoint p_ausiliario;
}

// ============================================================================
// 2. RECEIVER
// ============================================================================

public class UDPReceiver : MonoBehaviour {

    [Header("Impostazioni Rete")]
    public int listenPort = 5005;
    private UdpClient udpClient;
    private Thread receiveThread;
    private volatile bool isRunning = true;

    [Header("Handshake verso il gateway Python")]
    public bool inviaHandshake = false;
    public string ipGateway = "192.168.1.100";
    public int portaGateway = 5006;
    public float intervalloHandshake = 2f;
    public bool acquisisciMulticastLock = false;

    [Header("Diagnostica")]
    public bool logDiagnostico = true;
    public float sogliaSilenzio = 3f;

    [Header("Modalita' Simulazione")]
    public bool modalitaSimulazione = false;
    public float raggioSimulazioneMm = 250f;
    public float velocitaSimulazione = 0.15f;

    [Header("Gestione coda")]
    public bool scartaTelemetriaArretrata = true;

    [Header("Impostazioni Visuali (Traiettorie)")]
    public LineRenderer lineRenderer;
    public float fadeDuration = 0.8f;
    public float distanceThreshold = 0.05f;

    [Header("Oggetto 3D da muovere")]
    public Transform robotMarker;

    [Header("Testi dell'Interfaccia HMI - Posizioni")]
    public TextMeshProUGUI textX;
    public TextMeshProUGUI textY;
    public TextMeshProUGUI textZ;

    [Header("Testi dell'Interfaccia HMI - Forze")]
    public TextMeshProUGUI textFx;
    public TextMeshProUGUI textFy;
    public TextMeshProUGUI textFz;

    [Header("Pannello o Canvas Interfaccia")]
    public GameObject hmiCanvas;

    [Header("Gestione input con mani visore VR")]
    public OVRHand RightHand;

    // UI control variables
    private bool isPinching = false;
    private float gesturecooldown = 0.5f;
    private float cooldownTimer = 0f;

    // Thread-safe queues
    private Queue<string> rawMessageQueue = new Queue<string>();
    private readonly object queueLock = new object();
    private Queue<KukaMessage> trajectoryQueue = new Queue<KukaMessage>();

    // Trajectory state variables
    private Vector3 currentRobotPosition;
    private Vector3 currentRobotForce; // Variable that stores the forces
    private bool isDrawing = false;
    private bool isFading = false;
    private Vector3 activeTargetPosition;
    private Color originalLineColor;

    // New internal state
    private readonly List<string> batchTemporaneo = new List<string>();
    private int pacchettiRicevuti = 0;
    private float ultimoPacchettoTime = -999f;
    private float timerStatistiche = 0f;
    private bool silenzioSegnalato = false;
    private bool reteChiusa = false;
    private float tempoSimulazione = 0f;
    private float timerTraiettoriaSimulata = 0f;
    private AndroidJavaObject multicastLock;

    // ========================================================================
    // STARTUP
    // ========================================================================

    void Start() {
        if (lineRenderer == null) {
            lineRenderer = GetComponent<LineRenderer>();
        }

        if (lineRenderer != null) {
            lineRenderer.useWorldSpace = false;
            lineRenderer.positionCount = 0;
            if (lineRenderer.material != null) {
                originalLineColor = lineRenderer.material.color;
            }
        } else {
            Debug.LogWarning("[UDP] Nessun LineRenderer assegnato: le traiettorie non verranno disegnate.");
        }

        if (hmiCanvas != null) {
            hmiCanvas.SetActive(false);
        }

        if (modalitaSimulazione) {
            Debug.Log("[UDP] MODALITA' SIMULAZIONE attiva: la rete non viene avviata.");
            return;
        }

        if (acquisisciMulticastLock) AcquisisciMulticastLock();
        AvviaRete();
    }

    private void AvviaRete() {
        try {
            udpClient = new UdpClient(listenPort);
            udpClient.Client.ReceiveTimeout = 500;
        }
        catch (Exception e) {
            Debug.LogError("[UDP] Impossibile aprire la porta " + listenPort + ": " + e.Message);
            return;
        }

        isRunning = true;
        receiveThread = new Thread(new ThreadStart(ReceiveDataInBackground));
        receiveThread.IsBackground = true;
        receiveThread.Start();

        Debug.Log("[UDP] Gateway in ascolto sulla porta " + listenPort);

        if (inviaHandshake) StartCoroutine(RoutineHandshake());
    }

    private void ReceiveDataInBackground() {
        IPEndPoint anyIP = new IPEndPoint(IPAddress.Any, 0);
        while (isRunning) {
            try {
                byte[] data = udpClient.Receive(ref anyIP);
                string jsonText = Encoding.UTF8.GetString(data);

                lock (queueLock) {
                    rawMessageQueue.Enqueue(jsonText);
                }
            }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }
            catch (Exception) { }
        }
    }

    private IEnumerator RoutineHandshake() {
        var attesa = new WaitForSeconds(Mathf.Max(0.5f, intervalloHandshake));
        while (isRunning) {
            try {
                string hello = "{\"tipo_messaggio\":\"hello\",\"porta\":" + listenPort + "}";
                byte[] payload = Encoding.UTF8.GetBytes(hello);
                if (udpClient != null) udpClient.Send(payload, payload.Length, ipGateway, portaGateway);
            }
            catch (Exception e) {
                Debug.LogWarning("[UDP] Handshake fallito verso " + ipGateway + ":" + portaGateway + " - " + e.Message);
            }
            yield return attesa;
        }
    }

    // ========================================================================
    // LOOP
    // ========================================================================

    void Update() {
        HandleInputAndPinch();

        if (modalitaSimulazione) GeneraDatiSimulati();
        else ProcessNetworkMessages();

        ManageTrajectoryQueue();
        CheckTrajectoryCompletion();

        if (logDiagnostico && !modalitaSimulazione) AggiornaDiagnostica();
    }

    private void HandleInputAndPinch() {
        if (cooldownTimer > 0) {
            cooldownTimer -= Time.deltaTime;
        }

        if (Input.GetKeyDown(KeyCode.M)) {
            ToggleInterfaccia();
        }

        if (RightHand != null && RightHand.IsDataValid) {
            bool pinchAttuale = RightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);

            if (pinchAttuale && !isPinching && cooldownTimer <= 0) {
                ToggleInterfaccia();
                cooldownTimer = gesturecooldown;
            }
            isPinching = pinchAttuale;
        }
    }

    public void ToggleInterfaccia() {
        if (hmiCanvas != null) {
            bool statoAttuale = hmiCanvas.activeSelf;
            hmiCanvas.SetActive(!statoAttuale);
        }
    }

    // ========================================================================
    // NETWORK DATA PROCESSING
    // ========================================================================

    private void ProcessNetworkMessages() {
        batchTemporaneo.Clear();

        lock (queueLock) {
            if (scartaTelemetriaArretrata) {
                while (rawMessageQueue.Count > 0) batchTemporaneo.Add(rawMessageQueue.Dequeue());
            }
            else if (rawMessageQueue.Count > 0) {
                batchTemporaneo.Add(rawMessageQueue.Dequeue());
            }
        }

        if (batchTemporaneo.Count == 0) return;

        pacchettiRicevuti += batchTemporaneo.Count;
        ultimoPacchettoTime = Time.time;
        silenzioSegnalato = false;

        KukaMessage ultimaTelemetria = null;

        for (int i = 0; i < batchTemporaneo.Count; i++) {
            try {
                KukaMessage msg = JsonUtility.FromJson<KukaMessage>(batchTemporaneo[i]);
                if (msg == null) continue;

                if (msg.tipo_messaggio == "telemetria") {
                    ultimaTelemetria = msg;
                }
                else if (msg.tipo_messaggio == "previsione") {
                    trajectoryQueue.Enqueue(msg);
                }
            }
            catch (Exception e) {
                Debug.LogWarning("Errore decodifica JSON: " + e.Message);
            }
        }

        if (ultimaTelemetria != null && ultimaTelemetria.posizione_attuale != null) {
            currentRobotPosition = ultimaTelemetria.posizione_attuale.ToVector3();
            
            // Store the native KUKA force data
            if (ultimaTelemetria.forza != null) {
                currentRobotForce = new Vector3(ultimaTelemetria.forza.x, ultimaTelemetria.forza.y, ultimaTelemetria.forza.z);
            }

            UpdateRobotAndUI();
        }
    }

    private void AggiornaDiagnostica() {
        timerStatistiche += Time.deltaTime;

        if (timerStatistiche >= 2f) {
            Debug.Log("[UDP] " + pacchettiRicevuti + " pacchetti in " + timerStatistiche.ToString("F1") + "s (~" +
                      (pacchettiRicevuti / timerStatistiche).ToString("F1") + " Hz) | traiettorie in coda: " + trajectoryQueue.Count);
            pacchettiRicevuti = 0;
            timerStatistiche = 0f;
        }

        if (!silenzioSegnalato && ultimoPacchettoTime > 0f && Time.time - ultimoPacchettoTime > sogliaSilenzio) {
            Debug.LogWarning("[UDP] Nessun dato da " + sogliaSilenzio + "s. Verifica IP del visore, isolamento client del WiFi e porta " + listenPort + ".");
            silenzioSegnalato = true;
        }
    }

    private void GeneraDatiSimulati() {
        tempoSimulazione += Time.deltaTime;
        float ang = tempoSimulazione * velocitaSimulazione * 2f * Mathf.PI;

        JsonPoint p = new JsonPoint {
            x = 600f + raggioSimulazioneMm * Mathf.Cos(ang),
            y = raggioSimulazioneMm * Mathf.Sin(ang),
            z = 400f + raggioSimulazioneMm * 0.4f * Mathf.Sin(ang * 2f)
        };

        currentRobotPosition = p.ToVector3();
        currentRobotForce = new Vector3(15.2f, -5.0f, 2.3f); // Dummy force for UI testing

        UpdateRobotAndUI();

        timerTraiettoriaSimulata += Time.deltaTime;
        if (timerTraiettoriaSimulata > 4f && !isDrawing && !isFading) {
            timerTraiettoriaSimulata = 0f;

            float angFuturo = ang + 1.2f;
            JsonPoint arrivo = new JsonPoint {
                x = 600f + raggioSimulazioneMm * Mathf.Cos(angFuturo),
                y = raggioSimulazioneMm * Mathf.Sin(angFuturo),
                z = 400f + raggioSimulazioneMm * 0.4f * Mathf.Sin(angFuturo * 2f)
            };

            trajectoryQueue.Enqueue(new KukaMessage {
                tipo_messaggio = "previsione",
                forma = "lineare",
                p_partenza = p,
                p_arrivo = arrivo
            });
        }
    }

    // ========================================================================
    // VISUAL COMPONENTS UPDATE
    // ========================================================================

    private void UpdateRobotAndUI() {
        if (robotMarker != null) {
            robotMarker.localPosition = currentRobotPosition;
        }

        if (hmiCanvas != null && hmiCanvas.activeSelf) {
        
            if (textX != null) textX.text = "Pos X: " + (currentRobotPosition.x).ToString("F3") + " m";
            if (textY != null) textY.text = "Pos Y: " + currentRobotPosition.y.ToString("F3") + " m";
            if (textZ != null) textZ.text = "Pos Z: " + currentRobotPosition.z.ToString("F3") + " m";

            
            if (textFx != null) textFx.text = "F_x: " + (-currentRobotForce.y).ToString("F1") + " N";
            if (textFy != null) textFy.text = "F_y: " + currentRobotForce.x.ToString("F1") + " N";
            if (textFz != null) textFz.text = "F_z: " + (currentRobotForce.z).ToString("F1") + " N";
        }
    }

    // ========================================================================
    // TRAJECTORIES AND SHUTDOWN 
    // ========================================================================
    
    private void ManageTrajectoryQueue() {
        if (!isDrawing && !isFading && trajectoryQueue.Count > 0) {
            KukaMessage nextTrajectory = trajectoryQueue.Dequeue();
            DrawIntent(nextTrajectory);
        }
    }

    private void CheckTrajectoryCompletion() {
        if (isDrawing && !isFading) {
            float distance = Vector3.Distance(currentRobotPosition, activeTargetPosition);

            if (distance <= distanceThreshold) {
                StartCoroutine(FadeOutLineCoroutine());
            }
        }
    }

    private void DrawIntent(KukaMessage msg) {
        if (lineRenderer == null) return;

        isDrawing = true;
        activeTargetPosition = msg.p_arrivo.ToVector3();
        Vector3 startPos = msg.p_partenza.ToVector3();

        if (msg.forma == "lineare") {
            lineRenderer.positionCount = 2;
            lineRenderer.SetPosition(0, startPos);
            lineRenderer.SetPosition(1, activeTargetPosition);
        }
        else if (msg.forma == "circolare") {
            Vector3 midPos = msg.p_ausiliario.ToVector3();
            Debug.Log($"[TRAIETTORIA] start={startPos} aux={midPos} end={activeTargetPosition}");
            DrawThreePointArc(startPos, midPos, activeTargetPosition);
        }
    }

    private void DrawThreePointArc(Vector3 start, Vector3 mid, Vector3 end) {
        Vector3 v1 = mid - start;
        Vector3 v2 = end - start;
        Vector3 n  = Vector3.Cross(v1, v2);

        if (n.magnitude < 1e-4f * v1.magnitude * v2.magnitude) {
            lineRenderer.positionCount = 3;
            lineRenderer.SetPosition(0, start);
            lineRenderer.SetPosition(1, mid);
            lineRenderer.SetPosition(2, end);
            return;
        }

        float k = 1f / (2f * n.sqrMagnitude);
        Vector3 center = start + k * (Vector3.Cross(n, v1) * v2.sqrMagnitude + Vector3.Cross(v2, n) * v1.sqrMagnitude);
        float raggio = Vector3.Distance(start, center);

        Vector3 nHat = n.normalized;
        Vector3 u = (start - center) / raggio;
        Vector3 w = Vector3.Cross(nHat, u);

        float aMid = AngoloNelPiano(mid, center, u, w);
        float aEnd = AngoloNelPiano(end, center, u, w);

        float sweep = (aEnd < aMid) ? aEnd - 2f * Mathf.PI : aEnd;
        int segments = Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(sweep) / 0.035f), 8, 120);
        
        lineRenderer.positionCount = segments + 1;
        for (int i = 0; i <= segments; i++) {
            float a = sweep * i / segments;
            lineRenderer.SetPosition(i, center + raggio * (Mathf.Cos(a) * u + Mathf.Sin(a) * w));
        }
    }

    private static float AngoloNelPiano(Vector3 p, Vector3 center, Vector3 u, Vector3 w) {
        Vector3 d = p - center;
        float a = Mathf.Atan2(Vector3.Dot(d, w), Vector3.Dot(d, u));
        return a < 0f ? a + 2f * Mathf.PI : a;
    }

    private IEnumerator FadeOutLineCoroutine() {
        isFading = true;
        float timeElapsed = 0;
        Color currentColor = lineRenderer.material.color;

        while (timeElapsed < fadeDuration) {
            timeElapsed += Time.deltaTime;
            float newAlpha = Mathf.Lerp(originalLineColor.a, 0f, timeElapsed / fadeDuration);
            currentColor.a = newAlpha;
            lineRenderer.material.color = currentColor;
            yield return null;
        }

        lineRenderer.positionCount = 0;
        lineRenderer.material.color = originalLineColor;
        isDrawing = false;
        isFading = false;
    }

    private void ChiudiRete() {
        if (reteChiusa) return;
        reteChiusa = true;
        isRunning = false;
        try { if (udpClient != null) udpClient.Close(); } catch (Exception) { }
        udpClient = null;
        try { if (receiveThread != null && receiveThread.IsAlive) receiveThread.Join(1000); } catch (Exception) { }
        receiveThread = null;
        RilasciaMulticastLock();
        Debug.Log("[UDP] Rete chiusa correttamente.");
    }

    void OnApplicationQuit() { ChiudiRete(); }
    void OnDestroy() { ChiudiRete(); }

    private void AcquisisciMulticastLock() {
#if UNITY_ANDROID && !UNITY_EDITOR
        try {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
            using (var wifiManager = context.Call<AndroidJavaObject>("getSystemService", "wifi")) {
                multicastLock = wifiManager.Call<AndroidJavaObject>("createMulticastLock", "kukaMrLock");
                multicastLock.Call("setReferenceCounted", true);
                multicastLock.Call("acquire");
                Debug.Log("[UDP] MulticastLock acquisito.");
            }
        }
        catch (Exception e) { Debug.LogWarning("[UDP] MulticastLock non acquisito: " + e.Message); }
#endif
    }

    private void RilasciaMulticastLock() {
#if UNITY_ANDROID && !UNITY_EDITOR
        try {
            if (multicastLock != null) {
                multicastLock.Call("release");
                multicastLock.Dispose();
                multicastLock = null;
            }
        } catch (Exception) { }
#endif
    }
}