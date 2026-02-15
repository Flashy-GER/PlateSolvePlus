# PlateSolvePlus Changelog

## 1.0.2.0
## ✨ Features, Improvements & Bug Fixes

### Secondary Camera Autofocus
- Fully integrated autofocus workflow for secondary cameras
- Support for backlash strategies (Overshoot / One-Way-Approach)
- Proper status and progress feedback during and after autofocus runs

### Bug Fixes
- fix autofoucs settings not being applied correctly
- UI may now be updated during autofocus runs (status, progress, button states)
- Sequencer imporments

### Improved Sequencer Integration
- Targets are only sent to the sequencer when a sequence is present and loaded
- Prevents errors such as “no sequence available”

## 1.0.1.2 (BETA 2)
## ✨ Features, Improvements & Bug Fixes

### NEW: Secondary Camera Autofocus
- Fully integrated autofocus workflow for secondary cameras
- Support for backlash strategies (Overshoot / One-Way-Approach)
- Proper status and progress feedback during and after autofocus runs

### Improved Sequencer Integration
- Targets are only sent to the sequencer when a sequence is present and loaded
- Prevents errors such as “no sequence available”

### Alpaca Camera Improvements
- Correct parameter handling for PUT requests (form body instead of query parameters)
- Improved robustness during connect and capture operations
- Fixed invalid subframe handling during Alpaca captures

### UX & UI Improvements
- Consistent enable/disable behavior for buttons based on device state
- Improved status indicators for autofocus and capture workflows
- Clearer user feedback during running actions (“Action running… please wait”)

### Bug Fixes
- Fixed disabled Cancel button during secondary autofocus
- Autofocus status no longer remains stuck in “running” state after completion
- Fixed enum comparisons for backlash modes
- Prevented UI deadlocks when actions are canceled or fail

### Internal / Refactoring
- Cleanup of service interfaces and state handling
- Improved separation between UI state and device state
- Groundwork for future autofocus and sequencer enhancements

## 1.0.1.1 (BETA 1)

### NEW: Secondary Camera Autofocus
- Fully integrated autofocus workflow for secondary cameras
- Support for backlash strategies (Overshoot / One-Way-Approach)
- Proper status and progress feedback during and after autofocus runs

## 1.0.0.4
## 🐛 Bug Fixes & Optimizations

### NEW: Slew to Target and Solve
(Target must be set in the Framing Assistant, then select the target and load the image)

### UX Fixes and Validations
- Busy / idle state handling
- Buttons are disabled while an action is running


## 1.0.0.3
## 🐛 Bug Fixes & Improvements

### Bug Fix: Incorrect Offset Display After Reset
- Fixed an issue where old offset values were still shown in the UI after resetting the offset.
- The offset display now updates correctly after a reset.

### Bug Fix: Incorrect Quaternion Calculation
- Fixed an issue where the quaternion was calculated using outdated offset values.
- The quaternion is now correctly derived from the current rotation values.
- The quaternion property now always reflects the current rotation state.

### Improved UI Performance
- Optimizations to improve UI responsiveness during rapid offset changes.
- Reduced unnecessary UI updates through improved `PropertyChanged` logic.
- Offset values in **Options** now update more efficiently.
- Live updates and preview functionality were optimized for a smoother user experience.

### Improved Touch-N-Stars Integration
- Additional adjustments to improve compatibility with Touch-N-Stars.
- Bug fixes and optimizations for the TnS API integration.
- Improved synchronization of offset data between PlateSolvePlus and Touch-N-Stars.
- Stability improvements in communication with Touch-N-Stars.

### Updated Translations
- Updated translations for all new and modified UI elements (DE, EN).


## 1.0.0.2
## 🔧 Offset Handling & UI Refactoring

### Simplified Offset Logic
- The offset is now automatically applied as soon as a valid offset is set  
  (rotation ≠ identity or ΔRA / ΔDec ≠ 0).
- The explicit *Offset On/Off* switch has been completely removed.

### Cleaned Up CaptureOnly Workflow
- **CaptureOnly** now performs a platesolve only and no longer modifies any system state.
- When an offset is present, corrected coordinates are calculated and compared with the current mount position.
- Deviations are displayed along with a recommendation to recalibrate the offset.

### Options.xaml as Central Offset View
- The offset display (rotation, quaternion, ΔRA / ΔDec, last calibration timestamp) has been fully moved to **Options**.
- New **“Reset Offset”** button to completely reset the offset.

### PlatesolveplusDockableView Removed
- The obsolete dockable and its associated UI logic have been decoupled and can be fully removed.

### Live UI Updates for Offset Values
- Rotation and offset values now update immediately in **Options** after reset or calibration.
- Correct `PropertyChanged` cascades ensure consistent UI updates for calculated properties.

### REST / API & Centering Adjustments
- **Capture+Sync**, **Capture+Slew**, and **Target+Center** now use the offset only when a valid offset is set.
- Centering is only possible when a valid offset is available.

### Touch-N-Stars Integration
- APIs adjusted for Touch-N-Stars.


## 1.0.0.1
- Initial alpha release
