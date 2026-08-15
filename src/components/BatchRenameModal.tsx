import React, { useState, useEffect } from 'react';
import {
  FileEdit,
  X,
  Check,
  AlertTriangle,
  ArrowRight,
  RefreshCw,
  Hash,
  Type,
  ListOrdered,
} from 'lucide-react';
import type { BatchRenameRule, BatchRenamePreviewItem } from '../types/explorer';
import { fileService } from '../services/fileService';

interface BatchRenameModalProps {
  selectedPaths: string[];
  isOpen: boolean;
  onClose: () => void;
  onSuccess: () => void;
}

export const BatchRenameModal: React.FC<BatchRenameModalProps> = ({
  selectedPaths,
  isOpen,
  onClose,
  onSuccess,
}) => {
  const [mode, setMode] = useState<BatchRenameRule['mode']>('replace');
  const [findText, setFindText] = useState('');
  const [replaceText, setReplaceText] = useState('');
  const [isRegex, setIsRegex] = useState(false);
  const [caseSensitive, setCaseSensitive] = useState(false);
  const [prefix, setPrefix] = useState('');
  const [suffix, setSuffix] = useState('');
  const [startNumber, setStartNumber] = useState(1);
  const [padding, setPadding] = useState(3);
  const [caseMode, setCaseMode] = useState<'lower' | 'upper' | 'title'>('lower');

  const [previewItems, setPreviewItems] = useState<BatchRenamePreviewItem[]>([]);
  const [isExecuting, setIsExecuting] = useState(false);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  // Update preview whenever rules change
  useEffect(() => {
    if (!isOpen || selectedPaths.length === 0) return;

    const rule: BatchRenameRule = {
      mode,
      findText,
      replaceText,
      isRegex,
      caseSensitive,
      prefix,
      suffix,
      startNumber,
      padding,
      caseMode,
    };

    fileService
      .previewBatchRename(selectedPaths, rule)
      .then((items) => setPreviewItems(items))
      .catch((err) => console.error(err));
  }, [
    isOpen,
    selectedPaths,
    mode,
    findText,
    replaceText,
    isRegex,
    caseSensitive,
    prefix,
    suffix,
    startNumber,
    padding,
    caseMode,
  ]);

  if (!isOpen) return null;

  const handleExecute = async () => {
    setIsExecuting(true);
    setErrorMsg(null);
    try {
      const itemsToRename = previewItems
        .filter((item) => item.willChange && !item.hasConflict)
        .map((item) => ({
          originalPath: item.originalPath,
          newPath: item.newPath,
        }));

      const res = await fileService.executeBatchRename(itemsToRename);
      if (res.errors.length > 0) {
        setErrorMsg(res.errors.join('\n'));
      } else {
        onSuccess();
        onClose();
      }
    } catch (err: any) {
      setErrorMsg(err.message || 'Batch rename failed');
    } finally {
      setIsExecuting(false);
    }
  };

  const changedCount = previewItems.filter((i) => i.willChange).length;
  const conflictCount = previewItems.filter((i) => i.hasConflict).length;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 backdrop-blur-sm p-4">
      <div className="bg-[#0b101c] border border-[#1e293b] rounded-xl shadow-2xl w-full max-w-3xl overflow-hidden flex flex-col max-h-[85vh]">
        {/* Header */}
        <div className="flex items-center justify-between p-4 bg-[#0f1626] border-b border-[#1e293b]">
          <div className="flex items-center gap-2">
            <div className="p-1.5 rounded bg-[#0284c7]/20 text-[#38bdf8]">
              <FileEdit size={16} />
            </div>
            <div>
              <h2 className="text-sm font-bold text-[#f8fafc]">Power Batch Renamer</h2>
              <p className="text-[11px] text-[#94a3b8] font-mono-code">
                {selectedPaths.length} items queued for renaming
              </p>
            </div>
          </div>
          <button
            onClick={onClose}
            className="p-1 hover:bg-[#1c2b45] text-[#94a3b8] hover:text-white rounded"
          >
            <X size={15} />
          </button>
        </div>

        {/* Mode Selector */}
        <div className="flex bg-[#090d16] border-b border-[#1e293b] p-1.5 gap-1.5 text-xs">
          <button
            onClick={() => setMode('replace')}
            className={`flex items-center gap-1.5 px-3 py-1.5 rounded font-medium transition-colors ${
              mode === 'replace'
                ? 'bg-[#0284c7] text-white'
                : 'text-[#94a3b8] hover:bg-[#152035] hover:text-[#f8fafc]'
            }`}
          >
            <Type size={13} />
            <span>Find & Replace</span>
          </button>
          <button
            onClick={() => setMode('prefix_suffix')}
            className={`flex items-center gap-1.5 px-3 py-1.5 rounded font-medium transition-colors ${
              mode === 'prefix_suffix'
                ? 'bg-[#0284c7] text-white'
                : 'text-[#94a3b8] hover:bg-[#152035] hover:text-[#f8fafc]'
            }`}
          >
            <span>Prefix / Suffix</span>
          </button>
          <button
            onClick={() => setMode('numbering')}
            className={`flex items-center gap-1.5 px-3 py-1.5 rounded font-medium transition-colors ${
              mode === 'numbering'
                ? 'bg-[#0284c7] text-white'
                : 'text-[#94a3b8] hover:bg-[#152035] hover:text-[#f8fafc]'
            }`}
          >
            <ListOrdered size={13} />
            <span>Numbering Sequence</span>
          </button>
          <button
            onClick={() => setMode('change_case')}
            className={`flex items-center gap-1.5 px-3 py-1.5 rounded font-medium transition-colors ${
              mode === 'change_case'
                ? 'bg-[#0284c7] text-white'
                : 'text-[#94a3b8] hover:bg-[#152035] hover:text-[#f8fafc]'
            }`}
          >
            <span>Change Case</span>
          </button>
        </div>

        {/* Configuration Controls */}
        <div className="p-4 bg-[#0d1424] border-b border-[#1e293b] space-y-3 text-xs">
          {mode === 'replace' && (
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="block text-[11px] font-mono-code text-[#94a3b8] mb-1">
                  Find Text:
                </label>
                <input
                  type="text"
                  value={findText}
                  onChange={(e) => setFindText(e.target.value)}
                  placeholder="Text to find..."
                  className="w-full bg-[#090d16] border border-[#1e293b] focus:border-[#0284c7] rounded px-2.5 py-1.5 text-xs text-[#f8fafc] font-mono-code outline-none"
                />
              </div>
              <div>
                <label className="block text-[11px] font-mono-code text-[#94a3b8] mb-1">
                  Replace With:
                </label>
                <input
                  type="text"
                  value={replaceText}
                  onChange={(e) => setReplaceText(e.target.value)}
                  placeholder="Replacement..."
                  className="w-full bg-[#090d16] border border-[#1e293b] focus:border-[#0284c7] rounded px-2.5 py-1.5 text-xs text-[#f8fafc] font-mono-code outline-none"
                />
              </div>
              <div className="col-span-2 flex items-center gap-4 text-[11px] text-[#94a3b8]">
                <label className="flex items-center gap-1.5 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={isRegex}
                    onChange={(e) => setIsRegex(e.target.checked)}
                    className="accent-[#0284c7]"
                  />
                  <span>Regular Expression</span>
                </label>
                <label className="flex items-center gap-1.5 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={caseSensitive}
                    onChange={(e) => setCaseSensitive(e.target.checked)}
                    className="accent-[#0284c7]"
                  />
                  <span>Case Sensitive</span>
                </label>
              </div>
            </div>
          )}

          {mode === 'prefix_suffix' && (
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="block text-[11px] font-mono-code text-[#94a3b8] mb-1">
                  Add Prefix:
                </label>
                <input
                  type="text"
                  value={prefix}
                  onChange={(e) => setPrefix(e.target.value)}
                  placeholder="e.g. backup_"
                  className="w-full bg-[#090d16] border border-[#1e293b] focus:border-[#0284c7] rounded px-2.5 py-1.5 text-xs text-[#f8fafc] font-mono-code outline-none"
                />
              </div>
              <div>
                <label className="block text-[11px] font-mono-code text-[#94a3b8] mb-1">
                  Add Suffix:
                </label>
                <input
                  type="text"
                  value={suffix}
                  onChange={(e) => setSuffix(e.target.value)}
                  placeholder="e.g. _v2"
                  className="w-full bg-[#090d16] border border-[#1e293b] focus:border-[#0284c7] rounded px-2.5 py-1.5 text-xs text-[#f8fafc] font-mono-code outline-none"
                />
              </div>
            </div>
          )}

          {mode === 'numbering' && (
            <div className="grid grid-cols-4 gap-3">
              <div>
                <label className="block text-[11px] font-mono-code text-[#94a3b8] mb-1">
                  Prefix:
                </label>
                <input
                  type="text"
                  value={prefix}
                  onChange={(e) => setPrefix(e.target.value)}
                  placeholder="e.g. item_"
                  className="w-full bg-[#090d16] border border-[#1e293b] focus:border-[#0284c7] rounded px-2.5 py-1.5 text-xs text-[#f8fafc] font-mono-code outline-none"
                />
              </div>
              <div>
                <label className="block text-[11px] font-mono-code text-[#94a3b8] mb-1">
                  Start Number:
                </label>
                <input
                  type="number"
                  value={startNumber}
                  onChange={(e) => setStartNumber(parseInt(e.target.value) || 1)}
                  className="w-full bg-[#090d16] border border-[#1e293b] focus:border-[#0284c7] rounded px-2.5 py-1.5 text-xs text-[#f8fafc] font-mono-code outline-none"
                />
              </div>
              <div>
                <label className="block text-[11px] font-mono-code text-[#94a3b8] mb-1">
                  Digits Padding:
                </label>
                <input
                  type="number"
                  min={1}
                  max={6}
                  value={padding}
                  onChange={(e) => setPadding(parseInt(e.target.value) || 3)}
                  className="w-full bg-[#090d16] border border-[#1e293b] focus:border-[#0284c7] rounded px-2.5 py-1.5 text-xs text-[#f8fafc] font-mono-code outline-none"
                />
              </div>
              <div>
                <label className="block text-[11px] font-mono-code text-[#94a3b8] mb-1">
                  Suffix:
                </label>
                <input
                  type="text"
                  value={suffix}
                  onChange={(e) => setSuffix(e.target.value)}
                  placeholder="e.g. _final"
                  className="w-full bg-[#090d16] border border-[#1e293b] focus:border-[#0284c7] rounded px-2.5 py-1.5 text-xs text-[#f8fafc] font-mono-code outline-none"
                />
              </div>
            </div>
          )}

          {mode === 'change_case' && (
            <div className="flex gap-4">
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="radio"
                  name="caseMode"
                  checked={caseMode === 'lower'}
                  onChange={() => setCaseMode('lower')}
                  className="accent-[#0284c7]"
                />
                <span>lowercase</span>
              </label>
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="radio"
                  name="caseMode"
                  checked={caseMode === 'upper'}
                  onChange={() => setCaseMode('upper')}
                  className="accent-[#0284c7]"
                />
                <span>UPPERCASE</span>
              </label>
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="radio"
                  name="caseMode"
                  checked={caseMode === 'title'}
                  onChange={() => setCaseMode('title')}
                  className="accent-[#0284c7]"
                />
                <span>Title Case</span>
              </label>
            </div>
          )}
        </div>

        {/* Live Before / After Diff Table */}
        <div className="flex-1 overflow-y-auto p-3 font-mono-code text-xs">
          <div className="text-[11px] text-[#94a3b8] mb-2 flex items-center justify-between">
            <span>Live Before & After Preview:</span>
            <span>
              {changedCount} will change {conflictCount > 0 && `(${conflictCount} conflicts)`}
            </span>
          </div>

          <table className="w-full border-collapse">
            <thead>
              <tr className="bg-[#090d16] text-[#64748b] text-[10.5px]">
                <th className="text-left p-2 border-b border-[#1e293b]">Original Name</th>
                <th className="w-6 p-2 border-b border-[#1e293b]"></th>
                <th className="text-left p-2 border-b border-[#1e293b]">New Name</th>
                <th className="text-right p-2 border-b border-[#1e293b]">Status</th>
              </tr>
            </thead>
            <tbody>
              {previewItems.map((item) => (
                <tr
                  key={item.originalPath}
                  className={`border-b border-[#1e293b]/40 ${
                    item.hasConflict ? 'bg-[#ef4444]/10' : item.willChange ? 'bg-[#0284c7]/10' : ''
                  }`}
                >
                  <td className="p-2 text-[#94a3b8] truncate max-w-[200px]">{item.originalName}</td>
                  <td className="p-2 text-center text-[#64748b]">
                    <ArrowRight size={12} />
                  </td>
                  <td className="p-2 text-[#f8fafc] font-semibold truncate max-w-[240px]">
                    {item.newName}
                  </td>
                  <td className="p-2 text-right">
                    {item.hasConflict ? (
                      <span className="text-[#ef4444] text-[10px] font-bold flex items-center gap-1 justify-end">
                        <AlertTriangle size={11} />
                        Conflict
                      </span>
                    ) : item.willChange ? (
                      <span className="text-[#10b981] text-[10px] font-bold">Rename</span>
                    ) : (
                      <span className="text-[#64748b] text-[10px]">Unchanged</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          {errorMsg && (
            <div className="mt-3 p-2.5 rounded bg-[#ef4444]/15 border border-[#ef4444]/40 text-[#ef4444] text-xs whitespace-pre">
              {errorMsg}
            </div>
          )}
        </div>

        {/* Actions Footer */}
        <div className="p-3 bg-[#0f1626] border-t border-[#1e293b] flex items-center justify-between">
          <button
            onClick={onClose}
            className="px-3 py-1.5 rounded bg-[#1c2b45] hover:bg-[#334155] text-[#94a3b8] hover:text-white transition-colors text-xs"
          >
            Cancel
          </button>
          <button
            onClick={handleExecute}
            disabled={changedCount === 0 || conflictCount > 0 || isExecuting}
            className={`flex items-center gap-1.5 px-4 py-1.5 rounded text-xs font-semibold transition-colors ${
              changedCount > 0 && conflictCount === 0 && !isExecuting
                ? 'bg-[#0284c7] hover:bg-[#0369a1] text-white shadow-md'
                : 'bg-[#1e293b] text-[#64748b] cursor-not-allowed'
            }`}
          >
            <Check size={13} />
            <span>{isExecuting ? 'Renaming...' : `Apply Batch Rename (${changedCount})`}</span>
          </button>
        </div>
      </div>
    </div>
  );
};
