import React, { useState, useEffect, useRef } from 'react';
import { FolderPlus, FilePlus, X, Check } from 'lucide-react';

interface NewItemModalProps {
  isOpen: boolean;
  type: 'folder' | 'file';
  parentPath: string;
  onClose: () => void;
  onSubmit: (name: string) => Promise<void>;
}

export const NewItemModal: React.FC<NewItemModalProps> = ({
  isOpen,
  type,
  parentPath,
  onClose,
  onSubmit,
}) => {
  const [name, setName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (isOpen) {
      setName(type === 'folder' ? 'New Folder' : 'New File.txt');
      setError(null);
      setTimeout(() => {
        if (inputRef.current) {
          inputRef.current.focus();
          const dot = name.lastIndexOf('.');
          if (dot > 0) {
            inputRef.current.setSelectionRange(0, dot);
          } else {
            inputRef.current.select();
          }
        }
      }, 50);
    }
  }, [isOpen, type]);

  if (!isOpen) return null;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) {
      setError('Name cannot be empty');
      return;
    }
    try {
      await onSubmit(name.trim());
      onClose();
    } catch (err: any) {
      setError(err.message || 'Creation failed');
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 backdrop-blur-sm p-4">
      <div className="bg-[#0b101c] border border-[#1e293b] rounded-xl shadow-2xl w-full max-w-md overflow-hidden">
        <div className="flex items-center justify-between p-3.5 bg-[#0f1626] border-b border-[#1e293b]">
          <div className="flex items-center gap-2">
            {type === 'folder' ? (
              <FolderPlus size={16} className="text-[#38bdf8]" />
            ) : (
              <FilePlus size={16} className="text-[#38bdf8]" />
            )}
            <h3 className="text-sm font-semibold text-[#f8fafc]">
              Create New {type === 'folder' ? 'Folder' : 'File'}
            </h3>
          </div>
          <button
            onClick={onClose}
            className="p-1 hover:bg-[#1c2b45] text-[#94a3b8] hover:text-white rounded"
          >
            <X size={14} />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="p-4 space-y-3">
          <div>
            <label className="block text-[11px] font-mono-code text-[#94a3b8] mb-1">
              Destination Directory:
            </label>
            <div className="bg-[#090d16] px-2.5 py-1.5 rounded border border-[#1e293b] text-[#cbd5e1] font-mono-code text-[11px] truncate">
              {parentPath}
            </div>
          </div>

          <div>
            <label className="block text-[11px] font-mono-code text-[#94a3b8] mb-1">
              {type === 'folder' ? 'Folder Name' : 'File Name (with extension)'}:
            </label>
            <input
              ref={inputRef}
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              className="w-full bg-[#090d16] border border-[#1e293b] focus:border-[#0284c7] rounded px-3 py-1.5 text-xs font-mono-code text-[#f8fafc] outline-none"
            />
          </div>

          {error && <div className="text-[11px] text-[#ef4444] font-mono-code">{error}</div>}

          <div className="flex items-center justify-end gap-2 pt-2">
            <button
              type="button"
              onClick={onClose}
              className="px-3 py-1.5 rounded bg-[#1c2b45] hover:bg-[#334155] text-[#94a3b8] hover:text-white text-xs transition-colors"
            >
              Cancel
            </button>
            <button
              type="submit"
              className="flex items-center gap-1 px-4 py-1.5 rounded bg-[#0284c7] hover:bg-[#0369a1] text-white text-xs font-semibold shadow-md transition-colors"
            >
              <Check size={13} />
              <span>Create</span>
            </button>
          </div>
        </form>
      </div>
    </div>
  );
};
