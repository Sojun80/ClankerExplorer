import React, { useState, useEffect } from 'react';
import {
  FileText,
  Image as ImageIcon,
  Film,
  Music,
  Cpu,
  Hash,
  Copy,
  Check,
  X,
  ExternalLink,
  Code,
  FileCode,
  ShieldCheck,
} from 'lucide-react';
import type { FilePreviewData, HashResult } from '../types/explorer';
import { fileService } from '../services/fileService';

interface PreviewPanelProps {
  selectedPath: string | null;
  onClose: () => void;
}

export const PreviewPanel: React.FC<PreviewPanelProps> = ({ selectedPath, onClose }) => {
  const [previewData, setPreviewData] = useState<FilePreviewData | null>(null);
  const [hashes, setHashes] = useState<HashResult | null>(null);
  const [isCalculatingHash, setIsCalculatingHash] = useState(false);
  const [copiedHash, setCopiedHash] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(false);

  useEffect(() => {
    if (!selectedPath) {
      setPreviewData(null);
      setHashes(null);
      return;
    }

    let isMounted = true;
    setIsLoading(true);
    setHashes(null);

    fileService
      .getPreviewData(selectedPath)
      .then((data) => {
        if (isMounted) {
          setPreviewData(data);
          setIsLoading(false);
        }
      })
      .catch((err) => {
        if (isMounted) {
          console.error(err);
          setIsLoading(false);
        }
      });

    return () => {
      isMounted = false;
    };
  }, [selectedPath]);

  const handleComputeHash = async () => {
    if (!selectedPath) return;
    setIsCalculatingHash(true);
    try {
      const res = await fileService.calculateHash(selectedPath);
      setHashes(res);
    } catch (err) {
      console.error(err);
    } finally {
      setIsCalculatingHash(false);
    }
  };

  const copyText = async (text: string, label: string) => {
    await fileService.writeClipboardText(text);
    setCopiedHash(label);
    setTimeout(() => setCopiedHash(null), 2000);
  };

  if (!selectedPath) {
    return (
      <aside className="w-80 bg-[#080c14] border-l border-[#1e293b] flex flex-col items-center justify-center p-6 text-center text-xs text-[#64748b] select-none">
        <Cpu size={32} className="text-[#1e293b] mb-2" />
        <span className="font-mono-code text-[11px] text-[#94a3b8]">Select a file to inspect</span>
        <span className="text-[10px] mt-1 text-[#475569]">Code, Hex, Images, Media, Hashes</span>
      </aside>
    );
  }

  return (
    <aside className="w-84 bg-[#080c14] border-l border-[#1e293b] flex flex-col h-full text-xs select-none overflow-hidden">
      {/* Header */}
      <div className="flex items-center justify-between p-2.5 bg-[#0f1626] border-b border-[#1e293b]">
        <div className="flex items-center gap-1.5 overflow-hidden">
          <FileCode size={14} className="text-[#38bdf8] shrink-0" />
          <span className="font-semibold text-[#f8fafc] truncate text-[12px]">
            {previewData?.name || 'File Inspector'}
          </span>
        </div>
        <button
          onClick={onClose}
          className="p-1 hover:bg-[#1c2b45] text-[#94a3b8] hover:text-white rounded"
        >
          <X size={13} />
        </button>
      </div>

      {/* Content Area */}
      <div className="flex-1 overflow-y-auto p-3 space-y-3 font-sans">
        {isLoading ? (
          <div className="flex items-center justify-center py-12 text-[#94a3b8] font-mono-code text-xs">
            Loading preview...
          </div>
        ) : previewData?.type === 'image' && previewData.mediaUrl ? (
          /* Image Preview */
          <div className="space-y-2">
            <div className="bg-[#0b0f17] border border-[#1e293b] rounded-lg p-2 flex items-center justify-center overflow-hidden min-h-[180px] max-h-[260px]">
              <img
                src={previewData.mediaUrl}
                alt={previewData.name}
                className="max-h-[240px] max-w-full object-contain rounded"
              />
            </div>
            <div className="text-[11px] font-mono-code text-[#94a3b8] flex justify-between bg-[#0f1626] p-2 rounded border border-[#1e293b]">
              <span>Format: {previewData.extension.toUpperCase()}</span>
              <span>Size: {previewData.formattedSize}</span>
            </div>
          </div>
        ) : previewData?.type === 'audio' && previewData.mediaUrl ? (
          /* Audio Preview */
          <div className="bg-[#0f1626] border border-[#1e293b] rounded-lg p-3 space-y-2">
            <div className="flex items-center gap-2 text-[#a855f7]">
              <Music size={16} />
              <span className="font-semibold">Audio Playback</span>
            </div>
            <audio controls src={previewData.mediaUrl} className="w-full h-8" />
          </div>
        ) : previewData?.type === 'video' && previewData.mediaUrl ? (
          /* Video Preview */
          <div className="space-y-2">
            <video
              controls
              src={previewData.mediaUrl}
              className="w-full max-h-[220px] bg-black rounded border border-[#1e293b]"
            />
          </div>
        ) : previewData?.type === 'text' && previewData.textContent ? (
          /* Text / Code Syntax Preview */
          <div className="space-y-1.5">
            <div className="flex items-center justify-between text-[10.5px] font-mono-code text-[#94a3b8]">
              <span>{previewData.lineCount} lines ({previewData.encoding})</span>
              <button
                onClick={() => copyText(previewData.textContent || '', 'Code')}
                className="flex items-center gap-1 hover:text-[#38bdf8] text-[#64748b]"
              >
                {copiedHash === 'Code' ? <Check size={11} /> : <Copy size={11} />}
                <span>{copiedHash === 'Code' ? 'Copied' : 'Copy All'}</span>
              </button>
            </div>
            <div className="bg-[#090d16] border border-[#1e293b] rounded p-2 overflow-x-auto max-h-[280px] font-mono-code text-[11px] leading-relaxed text-[#cbd5e1] whitespace-pre select-text">
              {previewData.textContent}
            </div>
          </div>
        ) : previewData?.type === 'binary' && previewData.hexData ? (
          /* Binary Hex Viewer */
          <div className="space-y-1.5">
            <div className="flex items-center justify-between text-[10.5px] font-mono-code text-[#94a3b8]">
              <span>HEX DUMP (First 4KB)</span>
              <span className="text-[#38bdf8]">Offset / Hex / ASCII</span>
            </div>
            <div className="bg-[#090d16] border border-[#1e293b] rounded p-2 overflow-x-auto max-h-[280px] font-mono-code text-[10px] leading-tight text-[#94a3b8] select-text">
              {previewData.hexData.map((row) => (
                <div key={row.offset} className="flex gap-2">
                  <span className="text-[#64748b]">{row.offset}</span>
                  <span className="text-[#38bdf8]">{row.hex}</span>
                  <span className="text-[#cbd5e1]">{row.ascii}</span>
                </div>
              ))}
            </div>
          </div>
        ) : (
          <div className="text-center py-6 text-[#64748b] font-mono-code text-[11px]">
            {previewData?.type === 'directory' ? 'Directory selected' : 'No preview available'}
          </div>
        )}

        {/* Metadata Details Card */}
        {previewData && (
          <div className="bg-[#0f1626] border border-[#1e293b] rounded-lg p-2.5 space-y-1.5 text-[11px] font-mono-code">
            <div className="flex justify-between text-[#64748b]">
              <span>File Size:</span>
              <span className="text-[#f8fafc] font-semibold">
                {previewData.formattedSize} ({previewData.size.toLocaleString()} B)
              </span>
            </div>
            <div className="flex justify-between text-[#64748b]">
              <span>Extension:</span>
              <span className="text-[#38bdf8] font-bold">
                {previewData.extension || 'None'}
              </span>
            </div>
            <div className="flex justify-between text-[#64748b]">
              <span>Modified:</span>
              <span className="text-[#cbd5e1]">{previewData.mtime}</span>
            </div>
          </div>
        )}

        {/* Checksum & Integrity Calculator */}
        <div className="bg-[#0f1626] border border-[#1e293b] rounded-lg p-2.5 space-y-2">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-1.5 text-[#38bdf8] font-mono-code font-bold text-[11px]">
              <Hash size={13} />
              <span>Checksum Generator</span>
            </div>
            {!hashes && (
              <button
                onClick={handleComputeHash}
                disabled={isCalculatingHash}
                className="px-2 py-0.5 rounded bg-[#0284c7] hover:bg-[#0369a1] text-white font-mono-code text-[10.5px] transition-colors"
              >
                {isCalculatingHash ? 'Calculating...' : 'Compute Hashes'}
              </button>
            )}
          </div>

          {hashes && (
            <div className="space-y-1.5 font-mono-code text-[10px]">
              {/* SHA-256 */}
              <div className="bg-[#090d16] p-1.5 rounded border border-[#1e293b]">
                <div className="flex justify-between text-[#64748b] mb-0.5">
                  <span className="font-bold text-[#10b981]">SHA-256</span>
                  <button
                    onClick={() => copyText(hashes.sha256, 'SHA256')}
                    className="hover:text-white flex items-center gap-1"
                  >
                    {copiedHash === 'SHA256' ? <Check size={10} /> : <Copy size={10} />}
                    <span>{copiedHash === 'SHA256' ? 'Copied' : 'Copy'}</span>
                  </button>
                </div>
                <div className="truncate text-[#cbd5e1] select-all">{hashes.sha256}</div>
              </div>

              {/* MD5 */}
              <div className="bg-[#090d16] p-1.5 rounded border border-[#1e293b]">
                <div className="flex justify-between text-[#64748b] mb-0.5">
                  <span className="font-bold text-[#f59e0b]">MD5</span>
                  <button
                    onClick={() => copyText(hashes.md5, 'MD5')}
                    className="hover:text-white flex items-center gap-1"
                  >
                    {copiedHash === 'MD5' ? <Check size={10} /> : <Copy size={10} />}
                    <span>{copiedHash === 'MD5' ? 'Copied' : 'Copy'}</span>
                  </button>
                </div>
                <div className="truncate text-[#cbd5e1] select-all">{hashes.md5}</div>
              </div>
            </div>
          )}
        </div>
      </div>
    </aside>
  );
};
