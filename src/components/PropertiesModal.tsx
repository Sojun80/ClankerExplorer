import React, { useState, useEffect } from 'react';
import { Info, X, Copy, Check, Hash, ShieldCheck, HardDrive, Calendar } from 'lucide-react';
import type { FileItem, HashResult } from '../types/explorer';
import { fileService } from '../services/fileService';

interface PropertiesModalProps {
  item: FileItem | null;
  isOpen: boolean;
  onClose: () => void;
}

export const PropertiesModal: React.FC<PropertiesModalProps> = ({ item, isOpen, onClose }) => {
  const [hashes, setHashes] = useState<HashResult | null>(null);
  const [isHashing, setIsHashing] = useState(false);
  const [copiedKey, setCopiedKey] = useState<string | null>(null);

  useEffect(() => {
    if (isOpen && item && !item.isDirectory) {
      setHashes(null);
    }
  }, [isOpen, item]);

  if (!isOpen || !item) return null;

  const handleComputeHash = async () => {
    setIsHashing(true);
    try {
      const res = await fileService.calculateHash(item.path);
      setHashes(res);
    } catch (err) {
      console.error(err);
    } finally {
      setIsHashing(false);
    }
  };

  const copyToClipboard = async (text: string, key: string) => {
    await fileService.writeClipboardText(text);
    setCopiedKey(key);
    setTimeout(() => setCopiedKey(null), 2000);
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 backdrop-blur-sm p-4">
      <div className="bg-[#0b101c] border border-[#1e293b] rounded-xl shadow-2xl w-full max-w-lg overflow-hidden flex flex-col">
        {/* Header */}
        <div className="flex items-center justify-between p-3.5 bg-[#0f1626] border-b border-[#1e293b]">
          <div className="flex items-center gap-2 overflow-hidden">
            <Info size={16} className="text-[#38bdf8] shrink-0" />
            <h3 className="text-sm font-semibold text-[#f8fafc] truncate">
              {item.name} Properties
            </h3>
          </div>
          <button
            onClick={onClose}
            className="p-1 hover:bg-[#1c2b45] text-[#94a3b8] hover:text-white rounded"
          >
            <X size={14} />
          </button>
        </div>

        {/* Content */}
        <div className="p-4 space-y-3.5 text-xs font-mono-code overflow-y-auto max-h-[70vh]">
          {/* File Overview */}
          <div className="bg-[#090d16] p-3 rounded-lg border border-[#1e293b] space-y-2">
            <div className="flex items-start justify-between gap-2">
              <span className="text-[#64748b]">Name:</span>
              <span className="text-[#f8fafc] font-bold text-right break-all">{item.name}</span>
            </div>

            <div className="flex items-start justify-between gap-2">
              <span className="text-[#64748b]">Full Path:</span>
              <div className="flex items-center gap-1.5 max-w-[75%]">
                <span className="text-[#38bdf8] text-right truncate" title={item.path}>
                  {item.path}
                </span>
                <button
                  onClick={() => copyToClipboard(item.path, 'path')}
                  title="Copy path"
                  className="hover:text-white text-[#64748b] p-0.5"
                >
                  {copiedKey === 'path' ? <Check size={11} className="text-[#10b981]" /> : <Copy size={11} />}
                </button>
              </div>
            </div>

            <div className="flex items-center justify-between">
              <span className="text-[#64748b]">Extension:</span>
              <span className="badge-ext">{item.extension || 'None'}</span>
            </div>

            <div className="flex items-center justify-between">
              <span className="text-[#64748b]">Type:</span>
              <span className="text-[#cbd5e1]">{item.isDirectory ? 'File Folder' : `${item.extension.toUpperCase()} File`}</span>
            </div>

            <div className="flex items-center justify-between">
              <span className="text-[#64748b]">Size:</span>
              <span className="text-[#f8fafc] font-bold">
                {item.formattedSize} {item.size > 0 && `(${item.size.toLocaleString()} bytes)`}
              </span>
            </div>
          </div>

          {/* Timestamps */}
          <div className="bg-[#090d16] p-3 rounded-lg border border-[#1e293b] space-y-1.5 text-[11px]">
            <div className="flex items-center justify-between">
              <span className="text-[#64748b]">Created:</span>
              <span className="text-[#cbd5e1]">{item.formattedCtime}</span>
            </div>
            <div className="flex items-center justify-between">
              <span className="text-[#64748b]">Modified:</span>
              <span className="text-[#cbd5e1]">{item.formattedMtime}</span>
            </div>
            <div className="flex items-center justify-between">
              <span className="text-[#64748b]">Accessed:</span>
              <span className="text-[#cbd5e1]">{item.formattedAtime}</span>
            </div>
          </div>

          {/* Attributes */}
          <div className="bg-[#090d16] p-3 rounded-lg border border-[#1e293b]">
            <span className="text-[#64748b] block mb-2 font-bold uppercase text-[10px]">
              Attributes & Flags:
            </span>
            <div className="flex flex-wrap gap-2">
              <span className={`badge-attr ${item.isHidden ? 'bg-[#f59e0b]/20 text-[#f59e0b]' : ''}`}>
                Hidden: {item.isHidden ? 'YES' : 'NO'}
              </span>
              <span className={`badge-attr ${item.isSystem ? 'bg-[#ef4444]/20 text-[#ef4444]' : ''}`}>
                System: {item.isSystem ? 'YES' : 'NO'}
              </span>
              <span className="badge-attr">Read-Only: {item.isReadOnly ? 'YES' : 'NO'}</span>
              <span className="badge-attr">Archive: {item.isArchive ? 'YES' : 'NO'}</span>
              {item.isSymbolicLink && (
                <span className="badge-attr bg-[#38bdf8]/20 text-[#38bdf8]">Symbolic Link</span>
              )}
            </div>
          </div>

          {/* Checksums */}
          {!item.isDirectory && (
            <div className="bg-[#090d16] p-3 rounded-lg border border-[#1e293b] space-y-2">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-1.5 text-[#38bdf8] font-bold text-[11px]">
                  <Hash size={13} />
                  <span>File Checksums</span>
                </div>
                {!hashes && (
                  <button
                    onClick={handleComputeHash}
                    disabled={isHashing}
                    className="px-2 py-0.5 rounded bg-[#0284c7] hover:bg-[#0369a1] text-white text-[10.5px]"
                  >
                    {isHashing ? 'Computing...' : 'Calculate'}
                  </button>
                )}
              </div>

              {hashes && (
                <div className="space-y-1.5 text-[10px]">
                  <div>
                    <div className="flex justify-between text-[#64748b]">
                      <span className="text-[#10b981] font-bold">SHA-256:</span>
                      <button
                        onClick={() => copyToClipboard(hashes.sha256, 'sha256')}
                        className="hover:text-white flex items-center gap-1"
                      >
                        {copiedKey === 'sha256' ? <Check size={10} /> : <Copy size={10} />}
                        <span>Copy</span>
                      </button>
                    </div>
                    <div className="truncate text-[#cbd5e1] select-all bg-[#0b101c] p-1 rounded border border-[#1e293b]/60">
                      {hashes.sha256}
                    </div>
                  </div>

                  <div>
                    <div className="flex justify-between text-[#64748b]">
                      <span className="text-[#f59e0b] font-bold">MD5:</span>
                      <button
                        onClick={() => copyToClipboard(hashes.md5, 'md5')}
                        className="hover:text-white flex items-center gap-1"
                      >
                        {copiedKey === 'md5' ? <Check size={10} /> : <Copy size={10} />}
                        <span>Copy</span>
                      </button>
                    </div>
                    <div className="truncate text-[#cbd5e1] select-all bg-[#0b101c] p-1 rounded border border-[#1e293b]/60">
                      {hashes.md5}
                    </div>
                  </div>
                </div>
              )}
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="p-3 bg-[#0f1626] border-t border-[#1e293b] flex justify-end">
          <button
            onClick={onClose}
            className="px-4 py-1.5 rounded bg-[#0284c7] hover:bg-[#0369a1] text-white text-xs font-semibold"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  );
};
