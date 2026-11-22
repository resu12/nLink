# README screenshot provenance

## Gallery

The user supplied these five screenshots on 16 November 2026 for the README during hotfix-2 review. The root README now shows only the home image; the other views remain here. The browser tabs in the screen-sharing image were blurred after the initial import. The original supplied files were left untouched.

| README image | Supplied file | View |
| --- | --- | --- |
| [Home](home-2026-11-16.png) | `nlink.png` | Home |
| [Helper](helper-2026-11-16.png) | `helper.png` | Helper preparing its address; Copy/Share disabled and QR placeholder |
| [Helpee](helpee-2026-11-16.png) | `helpee.png` | Empty helper-address entry |
| [Screen sharing](screenshare-2026-11-16.png) | `acreensharing.png` | Active Helpee screen preview with chat |
| [File transfer](file-transfer-2026-11-16.png) | `filetransfer.png` | Transfer in progress with Pause/Cancel |

The date identifies receipt of the images, not an independently verified capture time. The exact executable/commit was not supplied with them. They illustrate the UI and do not establish transfer completion, Tuna payload routing, throughput, remote-control operation or a CI/release pass. The helper image contains no ready address or QR code to mask.

## Preserved earlier captures

The following original 0.7.0 screenshots (CI #102, source `3a031df8`) are preserved byte-for-byte in `docs/archive/images/` and are no longer embedded in the root README:

| Image | View | Privacy treatment |
| --- | --- | --- |
| [home-0.7.0.png](../archive/images/home-0.7.0.png) | Home | None |
| [helper-0.7.0.png](../archive/images/helper-0.7.0.png) | Helper waiting for requests | Address and QR covered with opaque masks |
| [helpee-0.7.0.png](../archive/images/helpee-0.7.0.png) | Helpee address entry | Empty address field |

These earlier captures do not show the hotfix-2 preparing-address state or fixed-position Wallet feedback, and are not runtime or release qualification.

The historical `*-0.4.5.png` files are also preserved byte-for-byte in [the image archive](../archive/images/). They are no longer embedded in the current README.

## Remaining scope

Wallet feedback and remote-control screenshots were not supplied. The earlier automated screenshot-only attempt on 16 November 2026 failed to initialize before any application or capture-helper launch; the user-supplied images above are separate from that attempt.

Use the selected portable's real UI for replacements; record its source/payload identity and mask addresses, QR codes, wallet details and unrelated desktop content. A screenshot session must not be described as a test campaign or release pass.
