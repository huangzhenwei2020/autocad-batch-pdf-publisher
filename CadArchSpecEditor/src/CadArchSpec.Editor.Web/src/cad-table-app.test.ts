import { describe, expect, it, vi } from "vitest";
import { revealCadTableEditor } from "./cad-table-app";

describe("standalone CAD table entry", () => {
  it("acknowledges a ready table immediately without scheduling a timer", () => {
    const postMessage = vi.fn();
    const setTimeoutSpy = vi.spyOn(globalThis, "setTimeout");

    revealCadTableEditor({ postMessage });

    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(postMessage.mock.calls[0][0]).toMatchObject({
      protocolVersion: 1,
      type: "cad.table.visible",
      payload: {},
    });
    expect(setTimeoutSpy).not.toHaveBeenCalled();
    setTimeoutSpy.mockRestore();
  });
});
