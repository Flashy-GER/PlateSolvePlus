# PlateSolvePlus – Schritt-für-Schritt Anleitung

Diese Anleitung richtet sich an Anwender, die PlateSolvePlus als **Fallback-Platesolving** nutzen wollen:
Wenn Plate Solving über **Hauptkamera/Hauptteleskop** schwierig ist (z. B. lange Brennweite), löst du über **Guiding-/Secondary-Kamera** und lässt das **Hauptteleskop** trotzdem präzise zentrieren.

---

## 1) Voraussetzungen

- N.I.N.A. installiert
- Plugin **PlateSolvePlus** installiert und in NINA geladen
- **Teleskop/Mount** in NINA verbunden
- **Secondary Camera** (z. B. Guide Cam) verfügbar und in PlateSolvePlus auswählbar
- Mindestens ein Plate Solver in NINA eingerichtet (z. B. ASTAP)

> Tipp: Wenn du ASTAP nutzt, achte auf korrekte Brennweite/Pixelgröße – bei sehr kleinen Bildern kann ASTAP Warnungen zu Scale/FOV anzeigen.

---

## 2) Einmalig: Grundkonfiguration in `Options`

Öffne NINA → `Options` → Plugin **PlateSolvePlus**.

### A) Guider Capture
- **Exposure (sec)**: Startwert 1–3 s (je nach Himmel und Guiderscope)
- **Gain**: -1 (Auto) oder ein sinnvoller fixer Wert
- **Binning**: 1 (Standard), nur erhöhen wenn nötig

### B) Optics / Image Scale
- **Focal Length (mm)**: Brennweite deines Guiding-Scopes
- **Pixel Size (µm)**:
  - Wenn „Use Camera Pixel Size“ aktiv ist, nutzt PlateSolvePlus den Wert der Kamera
  - sonst trage den Pixelwert manuell ein

### C) Plate Solver
- **Search Radius (deg)**: Startwert 5° (bei gutem Initial-Hint auch kleiner möglich)
- **Downsample**: 2 als guter Start
- **Timeout**: 60 s

### D) Centering (wie in NINA)
- **Centering Threshold (arcmin)**: Startwert 1.0
- **Max Attempts**: Startwert 5

---

## 3) Secondary Camera verbinden

Im **Imaging-Tab** (PlateSolvePlus Dockable):

1. Secondary Camera aus der Liste auswählen  
2. `Connect` drücken  
3. Optional: `Driver Settings` prüfen (ROI/Format, falls relevant)

---

## 4) Offset einrichten (empfohlen)

Damit das **Hauptteleskop** genau dahin zeigt, wo das **Guiding-Scope** platesolved, brauchst du den Offset.

### Offset aktivieren
- Checkbox: **„Offset verwenden“**

### Offset Mode
- **Rotation (Quaternion)** (empfohlen)
- **Arcsec (ΔRA/ΔDec)** (optional, eher für einfache Setups)

---

## 5) Offset kalibrieren (Rotation empfohlen)

Ziel: PlateSolvePlus lernt die Beziehung zwischen Guiderscope und Hauptteleskop.

**Empfohlener Ablauf:**

1. Stelle sicher, dass **Mount verbunden** ist.
2. Richte das Hauptteleskop grob auf ein sternreiches Feld.
3. Öffne PlateSolvePlus Dockable.
4. Drücke **Calibrate Offset**.
5. Warte auf Erfolgsmeldung und prüfe, ob der Offset als „gesetzt“ angezeigt wird.

> Wenn „Calibrate Offset“ keine Mount-Koordinaten lesen kann, prüfe:
> - Ist der Mount in NINA wirklich verbunden?
> - Liefert der Treiber RA/Dec (nicht geparkt, nicht im Fehlerzustand)?
> - Ist der richtige Mediator/Service eingebunden?

---

## 6) Workflow im Alltag

### A) Nur testen: `Capture`
- macht Aufnahme + Solve
- zeigt Koordinaten und Solve-Output
- verändert den Mount nicht

### B) Schnell ausrichten: `Capture + Sync`
- Solve über Secondary Camera
- Sync (wenn möglich), sonst Offset-Korrektur
- gut für „ich bin grob daneben“ oder Initial-Setup

### C) Präzise zentrieren: `Capture + Slew` (Centering)
- iteratives Centering wie NINA
- Solve → Fehler messen → Korrektur-Slew
- endet bei Threshold (arcmin) oder nach Max Attempts

---

## 7) Empfohlene Startwerte (Praxis)

- Exposure: 2 s
- Search Radius: 5°
- Downsample: 2
- Timeout: 60 s
- Centering Threshold: 1.0 arcmin
- Max Attempts: 5

Wenn es häufig nicht konvergiert:
- Threshold auf 2–3 arcmin erhöhen (erstmal stabil bekommen)
- Max Attempts auf 7–10 erhöhen
- Offset neu kalibrieren

---

## 8) „Checkliste“, wenn es nicht funktioniert

- Secondary Camera verbunden?
- Brennweite/Pixelgröße korrekt?
- Plate Solver in NINA funktioniert generell?
- Mount liefert RA/Dec?
- Offset gesetzt und aktiviert?
- Search Radius groß genug?

Weiteres siehe: **FAQ**.
