/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import React from "react";

type Props = {
  transcoding: boolean;
  progress: number;
};

export function TranscodeIndicator({ transcoding, progress }: Props) {
  if (!transcoding) return null;

  const pct = Math.round(progress * 100);

  return (
    <div className="transcode-indicator">
      <div className="transcode-spinner" />
      <span>{pct > 0 ? `${pct}%` : "Transcoding…"}</span>
    </div>
  );
}
