# PlateSolvePlus – FAQ

## Warum brauche ich PlateSolvePlus überhaupt?
Wenn dein Hauptsetup (Hauptteleskop/Hauptkamera) durch sehr kleine Felder oder lange Brennweiten schlecht platesolved,
kann ein Guiding-Scope mit großem Feld zuverlässig lösen. PlateSolvePlus nutzt das als Fallback.

---

## Ich drücke „Capture + Slew“ und das Teleskop bewegt sich nicht
Typische Ursachen:
- Mount ist nicht verbunden
- Mount ist geparkt / Tracking aus / Treiber blockt Slews
- Koordinaten werden nicht korrekt an den Mount übergeben (z. B. falsche RA/Dec-Einheiten)
- Solver liefert zwar ein Ergebnis, aber es ist offensichtlich falsch (z. B. falsche Brennweite/Pixelgröße)

Was tun:
1. Prüfe in NINA: Mount verbunden, nicht geparkt, Tracking an
2. Teste einen manuellen Slew aus NINA heraus
3. Prüfe PlateSolvePlus Status-/Details-Text (Target-RA/Dec)
4. Prüfe Optics/Scale Werte in den PlateSolvePlus Options

---

## ASTAP zeigt „Warning inexact scale“ / „small image dimensions“
Das ist meist ein Hinweis, dass:
- FOV/Scale nicht gut passt (Brennweite/Pixelgröße falsch)
- das Bild sehr klein ist (ROI, kleines Sensorfenster, starke Crops)

Lösungen:
- Brennweite Guiderscope prüfen
- Pixelgröße prüfen (Use Camera Pixel Size aktivieren oder korrekt eintragen)
- ROI/Crop deaktivieren (Test)
- Downsample reduzieren oder erhöhen (je nach Situation)

---

## Wann nutze ich „Capture + Sync“ und wann „Capture + Slew“?
- **Capture + Sync**: schneller, gut für grobe Ausrichtung / Setup / Kalibrierung
- **Capture + Slew**: präzises Centering (iterativ) vor der Session / vor Belichtung

---

## Muss ich den Offset kalibrieren?
Wenn du willst, dass das **Hauptteleskop** exakt da landet, wo das Guiding-Scope platesolved: **ja**.
Ohne Offset ist das Ergebnis meist daneben, weil Guide- und Hauptteleskop nicht parallel sind.

---

## Was ist besser: Rotation (Quaternion) oder Arcsec?
**Rotation (Quaternion)** ist in der Praxis meist stabiler:
- besser bei Rotation/Verkippung
- robuster bei Meridian Flip

**Arcsec (ΔRA/ΔDec)** ist einfacher, kann aber bei komplexeren Geometrien schlechter passen.

---

## Kann ich PlateSolvePlus nutzen, während die Hauptkamera belichtet?
In vielen Setups ja, weil PlateSolvePlus über die Secondary Camera arbeitet.
Aber: Bewegungen (Slew) beeinflussen natürlich die Hauptbelichtung.
Nutze Centering also typischerweise **vor** der Belichtungsserie.

---

## Centering konvergiert nicht (endet bei Max Attempts)
Ursachen:
- Offset ungenau oder nicht passend zum Setup
- Solver-Ergebnisse springen (zu kurze Belichtungszeit, zu wenig Sterne)
- Suchradius zu klein / falsche Scale

Lösungen:
- Offset neu kalibrieren
- Exposure erhöhen (z. B. 2 → 3–5 s)
- Threshold temporär erhöhen (z. B. 1 → 2–3 arcmin)
- Max Attempts erhöhen (z. B. 5 → 7–10)

---

## „Calibrate Offset“ meldet, dass keine Mount-Koordinaten gelesen werden können
Das passiert, wenn der Mount zwar „connected“ wirkt, aber RA/Dec nicht verfügbar sind.
Prüfe:
- Mount wirklich verbunden?
- Treiber liefert RA/Dec (nicht geparkt / nicht im Error)
- NINA zeigt RA/Dec im Telescope Panel an?

---

## Wie erkenne ich, ob der Offset aktiv ist?
- In den Options: Checkbox „Offset verwenden“
- Im Dockable: „Corrected“ bzw. „Desired(main)“/„Solved(main)“ Texte zeigen, ob eine Korrektur angewendet wurde.

---

## Wo melde ich Bugs / wünsche Features?
Am besten dort, wo du den Code verwaltest (Repo/Issues).
Wenn du Logs postest: bitte immer die Zeilen rund um Solve/Sync/Slew und die verwendeten Settings (Exposure, FocalLength, PixelSize, SearchRadius).
