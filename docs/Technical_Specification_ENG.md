# HyperCache X Technical Specification (English Edition)

## 1. Overview
**HyperCache X (HCX)** is a high-performance storage acceleration engine designed to eliminate HDD bottlenecks. By leveraging a dedicated SSD cache and AI-based workload analysis, HCX provides near-SSD responsiveness for traditional hard drives.

---

## 2. System Components
### 2.1 Kernel Mode: HCXDriver.sys
- **Architecture**: Windows File System MiniFilter.
- **I/O Redirection**: Actively monitors I/O Request Packets (IRPs). When a block hit is confirmed in the cache map, the driver reroutes the request to the high-speed SSD storage.
- **Safety Mechanics**: Utilizes kernel-level SpinLocks (`MapLock`) to ensure thread-safe access to volume contexts during high-load I/O operations.

### 2.2 User Mode: HCX.Core Engine
- **Intelligent Filtering (AI)**: Scores I/O events based on frequency and access patterns. Identifies large sequential reads/writes that would otherwise cause unnecessary SSD wear and excludes them from the cache.
- **LRU Eviction Policy**: Implements a Least Recently Used (LRU) algorithm using atomic slot management. This ensures the most relevant data remains in the 1GB cache while old blocks are efficiently purged.

---

## 3. Communication Interface (IOCTL)
The communication between the UI/Engine and the Kernel Driver is handled via the following IOCTL codes:

- **IOCTL_HCX_SET_SSD (0x801)**: Initializes the SSD cache target.
- **IOCTL_HCX_ADD_MAP (0x802)**: Updates the kernel-side mapping table for accelerated blocks.
- **IOCTL_HCX_SYNC (0x804)**: Ensures all "dirty" cached blocks are flushed and synchronized with the physical volume.

---

## 4. Operational Specifications
- **Block Granularity**: 64 KB slots.
- **Redirection Logic**: Redirects both read and write operations, with write-back caching for maximum performance gain.
- **Simulation Layer**: Included for development and UI verification without requiring a live kernel driver.

---

## 5. UI and UX Design
- **Responsive Dashboard**: Features glowing indicators for real-time throughput and AI scores.
- **Task Tray Residency**: Allows background-only operation to minimize system interference while maintaining drive acceleration.
