import { bindValue, useValue } from "cs2/api";
import { milestone } from "cs2/bindings";
import { useLocalization } from "cs2/l10n";
import { Portal } from "cs2/ui";
import { CSSProperties, useEffect, useState } from "react";

const inSession$ = bindValue<boolean>("cs2mp", "inSession", false);
const AUTO_DISMISS_MS = 10_000;

const styles: Record<string, CSSProperties> = {
    anchor: {
        position: "fixed",
        left: "50%",
        bottom: "78rem",
        width: "330rem",
        maxWidth: "70%",
        transform: "translateX(-50%)",
        zIndex: 10003,
        pointerEvents: "none",
    },
    notice: {
        overflow: "hidden",
        color: "#ffffff",
        fontSize: "13rem",
        textAlign: "center",
        backgroundColor: "rgba(24, 33, 51, 0.94)",
        border: "1rem solid rgba(157, 193, 222, 0.35)",
        borderRadius: "4rem",
        boxShadow: "0 8rem 24rem rgba(0, 0, 0, 0.42)",
    },
    text: {
        padding: "8rem 12rem 7rem",
    },
    track: {
        height: "3rem",
        backgroundColor: "rgba(255, 255, 255, 0.10)",
    },
    bar: {
        height: "100%",
        backgroundColor: "#72c8f0",
        transition: "width 120ms linear",
    },
};

// The vanilla milestone dialog blocks its local simulation-continue response
// until clearUnlockedMilestone is fired. In multiplayer, dismiss it after a
// short grace period so an AFK player cannot hold the whole session paused.
export const MilestoneAutoDismiss = () => {
    const { translate } = useLocalization();
    const inSession = useValue(inSession$);
    const unlocked = useValue(milestone.unlockedMilestone$);
    const [remainingMs, setRemainingMs] = useState(AUTO_DISMISS_MS);
    const unlockedIndex = unlocked?.index ?? 0;
    const unlockedVersion = unlocked?.version ?? 0;
    const active = inSession && (unlockedIndex !== 0 || unlockedVersion !== 0);

    useEffect(() => {
        if (!active) return;

        const startedAt = Date.now();
        setRemainingMs(AUTO_DISMISS_MS);
        const interval = window.setInterval(() => {
            setRemainingMs(Math.max(0, AUTO_DISMISS_MS - (Date.now() - startedAt)));
        }, 100);
        const timeout = window.setTimeout(() => {
            window.clearInterval(interval);
            setRemainingMs(0);
            milestone.clearUnlockedMilestone();
        }, AUTO_DISMISS_MS);

        return () => {
            window.clearInterval(interval);
            window.clearTimeout(timeout);
        };
    }, [active, unlockedIndex, unlockedVersion]);

    if (!active) return null;

    const seconds = Math.max(1, Math.ceil(remainingMs / 1000));
    const message = (
        translate("CS2MP.UI.AutoContinueMilestone", "Continuing automatically in {0}s") ??
        "Continuing automatically in {0}s"
    ).replace("{0}", String(seconds));
    const progress = Math.max(0, Math.min(100, remainingMs / AUTO_DISMISS_MS * 100));

    return (
        <Portal>
            <div style={styles.anchor}>
                <div style={styles.notice}>
                    <div style={styles.text}>{message}</div>
                    <div style={styles.track}>
                        <div style={{ ...styles.bar, width: `${progress}%` }} />
                    </div>
                </div>
            </div>
        </Portal>
    );
};
