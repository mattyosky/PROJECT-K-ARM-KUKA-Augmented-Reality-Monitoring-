using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Android;
using TMPro;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Aligns the RobotRoot (KUKA machine zero) with the world tracked by the headset,
/// starting from the detection of a QR code rigidly attached to the robot's cart.
///
/// EVENT WIRING:
/// The OnTrackableAdded / OnTrackableRemoved methods are NOT hooked up in code.
/// They must be wired in the Inspector: MRUK > Scene Settings > Tracker Configuration,
/// press + on the corresponding callback, drag this component into it and
/// pick the method from the "Dynamic MRUKTrackable" section (NOT from Static Parameters).
/// Also remember to tick "QR Code Tracking Enabled" in the same section.
/// </summary>
public class MarkerManager : MonoBehaviour
{
    [Header("Assegnazioni")]
    [Tooltip("Lo zero macchina del KUKA. Tutto cio' che riceve coordinate dal robot deve essergli figlio e usare localPosition.")]
    public Transform robotRoot;

    [Tooltip("Contenuto testuale esatto del QR solidale al carrello. Lascia vuoto per accettare qualunque QR (solo in diagnostica).")]
    public string payloadAtteso = "KUKA_BASE";

    [Header("Riscontro visivo")]
    [Tooltip("Testo TextMeshPro su un canvas World Space, meglio se figlio del CenterEyeAnchor cosi' segue lo sguardo.")]
    public TMP_Text testoStato;

    [Tooltip("Opzionale: renderer della sfera indicatore. Cambia colore in base allo stato della calibrazione.")]
    public Renderer indicatoreVisivo;

    [Tooltip("Colore dell'indicatore prima della calibrazione.")]
    public Color coloreInAttesa = Color.red;

    [Tooltip("Colore dell'indicatore durante la raccolta dei campioni.")]
    public Color coloreInCorso = Color.yellow;

    [Tooltip("Colore dell'indicatore a calibrazione completata.")]
    public Color coloreCalibrato = Color.green;

    [Header("Offset marker -> base robot")]
    [Tooltip("POSA DEL MARKER ESPRESSA NEL FRAME DI RobotRoot, in metri e assi Unity. " +
             "Si ricava una volta sola durante il bootstrap con EstraiOffsetDaCalibrazioneCorrente.")]
    public Vector3 offsetPosizione = Vector3.zero;

    [Tooltip("Rotazione del marker rispetto al frame di RobotRoot, in gradi Euler.")]
    public Vector3 offsetRotazione = Vector3.zero;

    [Header("Campionamento")]
    [Tooltip("Numero di frame da mediare prima di applicare l'allineamento.")]
    public int campioniRichiesti = 45;

    [Tooltip("Se il marker si sposta piu' di questa distanza (metri) durante la raccolta, i campioni vengono scartati e si ricomincia.")]
    public float sogliaStabilita = 0.02f;

    [Tooltip("Timeout in secondi: oltre questo tempo senza raggiungere la stabilita', la raccolta viene abbandonata.")]
    public float timeoutRaccolta = 10f;

    [Header("Plausibilita'")]
    [Tooltip("Distanza massima ammessa (metri) fra la testa dell'utente e la base stimata. Oltre, la calibrazione viene rifiutata. 0 = disattivato.")]
    public float distanzaMassimaAmmessa = 8f;

    [Header("Diagnostica")]
    [Tooltip("Se attivo, logga OGNI trackable ricevuto senza filtrare e senza calibrare. Usalo per scoprire il payload del tuo QR.")]
    public bool modalitaDiagnostica = false;

    private const string UseScenePermission = "com.oculus.permission.USE_SCENE";

    private bool calibrazioneEffettuata = false;
    private bool raccoltaInCorso = false;
    private Coroutine raccoltaCorrente = null;

    // ------------------------------------------------------------------
    // VISUAL FEEDBACK
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes the status both to the console and to the panel inside the headset.
    /// Use this instead of Debug.Log for every message the operator needs to see.
    /// </summary>
    private void Stato(string messaggio)
    {
        Debug.Log("[MarkerManager] " + messaggio);
        if (testoStato != null) testoStato.text = messaggio;
    }

    private void Indicatore(Color c)
    {
        if (indicatoreVisivo != null) indicatoreVisivo.material.color = c;
    }

    // ------------------------------------------------------------------
    // PERMISSIONS
    // ------------------------------------------------------------------

    private void Start()
    {
        Indicatore(coloreInAttesa);
        Stato("In attesa del QR...");

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(UseScenePermission))
        {
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => Debug.Log("[MarkerManager] Permesso USE_SCENE concesso.");
            callbacks.PermissionDenied += _ => Stato("ERRORE: permesso USE_SCENE negato.");
            Permission.RequestUserPermission(UseScenePermission, callbacks);
        }
        else
        {
            Debug.Log("[MarkerManager] Permesso USE_SCENE gia' concesso.");
        }
#endif
        if (robotRoot == null)
            Stato("ERRORE: robotRoot non assegnato.");
    }

    // ------------------------------------------------------------------
    // MRUK CALLBACKS  (to be wired in Tracker Configuration)
    // ------------------------------------------------------------------

    public void OnTrackableAdded(MRUKTrackable trackable)
    {
        if (trackable == null) return;

        // Unconditional log BEFORE any filtering: it helps tell apart
        // "nothing is arriving" from "it arrives but I'm discarding it".
        Debug.Log($"[MarkerManager] Trackable ricevuto | tipo={trackable.TrackableType} | payload=\"{trackable.MarkerPayloadString}\"");

        if (modalitaDiagnostica)
        {
            Stato($"DIAGNOSTICA\ntipo: {trackable.TrackableType}\npayload: {trackable.MarkerPayloadString}");
            return;
        }

        if (trackable.TrackableType != OVRAnchor.TrackableType.QRCode)
        {
            Debug.Log("[MarkerManager] Ignorato: non e' un QR code.");
            return;
        }

        if (!string.IsNullOrEmpty(payloadAtteso) && trackable.MarkerPayloadString != payloadAtteso)
        {
            Stato($"QR ignorato: payload \"{trackable.MarkerPayloadString}\"");
            return;
        }

        if (calibrazioneEffettuata || raccoltaInCorso) return;

        Stato("QR riconosciuto.\nTieni fermo il visore...");
        Indicatore(coloreInCorso);
        raccoltaCorrente = StartCoroutine(RoutineRaccolta(trackable));
     
    }

    public void OnTrackableRemoved(MRUKTrackable trackable)
    {
        if (trackable == null) return;
        if (raccoltaInCorso && trackable.TrackableType == OVRAnchor.TrackableType.QRCode)
        {
            Stato("Marker perso: campionamento annullato.");
            Indicatore(coloreInAttesa);
            if (raccoltaCorrente != null) StopCoroutine(raccoltaCorrente);
            raccoltaInCorso = false;
        }
    }

    // ------------------------------------------------------------------
    // SAMPLING AND AVERAGING
    // ------------------------------------------------------------------

    private IEnumerator RoutineRaccolta(MRUKTrackable trackable)
    {
        raccoltaInCorso = true;

        var posizioni = new List<Vector3>(campioniRichiesti);
        var rotazioni = new List<Quaternion>(campioniRichiesti);
        float tempoTrascorso = 0f;

        while (posizioni.Count < campioniRichiesti)
        {
            // The trackable's GameObject may disappear if MRUK loses the detection.
            if (trackable == null || trackable.transform == null)
            {
                Stato("Trackable non piu' valido:\nraccolta interrotta.");
                Indicatore(coloreInAttesa);
                raccoltaInCorso = false;
                yield break;
            }

            Vector3 p = trackable.transform.position;
            Quaternion r = trackable.transform.rotation;

            // Stability check: if the marker "jumps", start over from scratch.
            if (posizioni.Count > 0 && Vector3.Distance(p, posizioni[posizioni.Count - 1]) > sogliaStabilita)
            {
                posizioni.Clear();
                rotazioni.Clear();
                Stato("Rilevazione instabile.\nRicomincio la raccolta...");
            }

            posizioni.Add(p);
            rotazioni.Add(r);

            // Progress shown to the operator, updated every 5 samples.
            if (posizioni.Count % 5 == 0)
                Stato($"Campionamento {posizioni.Count}/{campioniRichiesti}");

            tempoTrascorso += Time.deltaTime;
            if (tempoTrascorso > timeoutRaccolta)
            {
                Stato("TIMEOUT: rilevazione instabile.\nAvvicinati o migliora l'illuminazione.");
                Indicatore(coloreInAttesa);
                raccoltaInCorso = false;
                yield break;
            }

            yield return null;
        }

        Vector3 posMedia = MediaPosizioni(posizioni);
        Quaternion rotMedia = MediaRotazioni(rotazioni);

        raccoltaInCorso = false;
        ApplicaAllineamento(posMedia, rotMedia);
    }

    private static Vector3 MediaPosizioni(List<Vector3> v)
    {
        Vector3 somma = Vector3.zero;
        for (int i = 0; i < v.Count; i++) somma += v[i];
        return somma / v.Count;
    }

    /// <summary>
    /// Quaternion averaging: the components are summed after aligning their sign
    /// with the first sample (q and -q represent the same rotation, but summing them
    /// without aligning the sign makes them cancel each other out), then the result is normalized.
    /// Valid because the samples are all very close to each other.
    /// </summary>
    private static Quaternion MediaRotazioni(List<Quaternion> q)
    {
        Quaternion riferimento = q[0];
        float x = 0f, y = 0f, z = 0f, w = 0f;

        for (int i = 0; i < q.Count; i++)
        {
            Quaternion c = q[i];
            if (Quaternion.Dot(c, riferimento) < 0f)
                c = new Quaternion(-c.x, -c.y, -c.z, -c.w);

            x += c.x; y += c.y; z += c.z; w += c.w;
        }

        float norma = Mathf.Sqrt(x * x + y * y + z * z + w * w);
        if (norma < 1e-6f) return riferimento;
        return new Quaternion(x / norma, y / norma, z / norma, w / norma);
    }

    // ------------------------------------------------------------------
    // APPLICATION
    // ------------------------------------------------------------------

    private void ApplicaAllineamento(Vector3 markerPos, Quaternion markerRot)
    {
        if (robotRoot == null)
        {
            Stato("ERRORE: robotRoot non assegnato.");
            return;
        }

        // The marker's world pose is the composition: marker = root o offset
        // thus:    rootRot = markerRot * inv(offsetRot)
        //          rootPos = markerPos - rootRot * offsetPos
        Quaternion offsetRot = Quaternion.Euler(offsetRotazione);
        Quaternion rootRot = markerRot * Quaternion.Inverse(offsetRot);
        Vector3 rootPos = markerPos - rootRot * offsetPosizione;

        // Plausibility check: if the base ends up absurdly far away,
        // the detection was almost certainly noisy.
        if (distanzaMassimaAmmessa > 0f && Camera.main != null)
        {
            float d = Vector3.Distance(Camera.main.transform.position, rootPos);
            if (d > distanzaMassimaAmmessa)
            {
                Stato($"CALIBRAZIONE RIFIUTATA\nbase stimata a {d:F1} m (max {distanzaMassimaAmmessa} m)");
                Indicatore(coloreInAttesa);
                return;
            }
        }

        robotRoot.SetPositionAndRotation(rootPos, rootRot);
        calibrazioneEffettuata = true;

        Indicatore(coloreCalibrato);
        Stato($"CALIBRATO\npos {rootPos.x:F2}, {rootPos.y:F2}, {rootPos.z:F2}\nyaw {rootRot.eulerAngles.y:F1}");
    }

    // ------------------------------------------------------------------
    // BOOTSTRAP: offset measurement
    // ------------------------------------------------------------------

    /// <summary>
    /// To be used ONLY ONCE, when the RobotRoot has already been correctly aligned
    /// by another method (point-based calibration with the controller). It computes the
    /// marker pose in the RobotRoot frame and prints it: those values must then be copied into
    /// the offsetPosizione / offsetRotazione fields and will never change again, because the
    /// marker is rigidly attached to the cart carrying the robot.
    /// </summary>
    public void EstraiOffsetDaCalibrazioneCorrente(Transform markerTransform)
    {
        if (robotRoot == null || markerTransform == null)
        {
            Stato("ERRORE: servono robotRoot e marker validi per estrarre l'offset.");
            return;
        }

        Vector3 pos = robotRoot.InverseTransformPoint(markerTransform.position);
        Quaternion rot = Quaternion.Inverse(robotRoot.rotation) * markerTransform.rotation;

        Stato($"OFFSET MISURATO\npos {pos.ToString("F4")}\nrot {rot.eulerAngles.ToString("F2")}");
    }

    // ------------------------------------------------------------------
    // UTILITY
    // ------------------------------------------------------------------

    [ContextMenu("Reset calibrazione")]
    public void ResetCalibrazione()
    {
        if (raccoltaCorrente != null) StopCoroutine(raccoltaCorrente);
        calibrazioneEffettuata = false;
        raccoltaInCorso = false;
        Indicatore(coloreInAttesa);
        Stato("Calibrazione resettata.\nIn attesa di una nuova scansione.");
    }

    public bool CalibrazioneCompletata => calibrazioneEffettuata;
}