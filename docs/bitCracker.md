


### Step 1: Understanding the Bitcoin Private Key and Keyspace
A Bitcoin private key is a 256-bit (32-byte) number used to sign transactions, allowing the owner to spend the bitcoins associated with the corresponding public key and address. The private key is part of the secp256k1 elliptic curve cryptography system used by Bitcoin.

#### Private Key Range
- **Size**: A Bitcoin private key is 256 bits, or 32 bytes.
- **Valid Range**: The private key must be between 1 and the order of the secp256k1 curve, which is approximately `2^256`. The exact upper limit is:
  ```
  0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364140
  ```
  In decimal, this is:
  ```
  115,792,089,237,316,195,423,570,985,008,687,907,852,837,564,279,074,904,382,605,163,141,518,161,494,336
  ```
- **Keyspace**: The total number of possible private keys is approximately `2^256`, or:
  ```
  115,792,089,237,316,195,423,570,985,008,687,907,853,269,984,665,640,564,039,457,584,007,913,129,639,936
  ```
  This is an astronomically large number, making brute-forcing the entire keyspace infeasible.

#### Private Key to Address
- **Private Key → Public Key**:
  - The private key is multiplied by the generator point on the secp256k1 curve to produce a public key (a point on the curve with x and y coordinates).
- **Public Key → Address**:
  - The public key is hashed using SHA-256, then RIPEMD-160, and encoded with a version byte and checksum to produce a Bitcoin address (e.g., a legacy address starting with "1", like `1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ`).
  - For a legacy address (P2PKH):
    1. Start with the public key.
    2. Compute `SHA-256(public_key)`.
    3. Compute `RIPEMD-160(SHA-256(public_key))`.
    4. Add a version byte (0x00 for mainnet).
    5. Add a checksum (first 4 bytes of `SHA-256(SHA-256(version + hash))`).
    6. Encode in Base58Check to get the address.

#### Keyspace in Cracking
- **Full Keyspace**: If you’re trying to crack a private key by guessing all possible 256-bit numbers, the keyspace is `2^256`. This is infeasible, as even the fastest supercomputers can’t search this space in a reasonable time (it would take billions of years).
- **Reduced Keyspace**: If you can constrain the keyspace (e.g., by starting with some known bytes or a specific range), the problem becomes more manageable, though still challenging.

---

### Step 2: Can We Start from Some Zero Bytes and Find the Wallet Address?
Your idea is to start with a private key where some of the bytes are zero (e.g., the first few bytes) and search for a private key that matches a target wallet address (e.g., `1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ`). Let’s break this down.

#### Starting with Zero Bytes
- **What It Means**:
  - A 256-bit private key is 32 bytes.
  - If you set some bytes to zero, you’re effectively reducing the number of bits you need to guess.
  - Example: If you set the first 16 bytes (128 bits) to zero, the private key looks like:
    ```
    00000000000000000000000000000000XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
    ```
    Where `X` represents the bytes you need to guess.
  - In this case, you’re only guessing the last 16 bytes (128 bits), reducing the keyspace significantly.

- **Reduced Keyspace**:
  - If the first 16 bytes are zero, you’re guessing 16 bytes (128 bits).
  - Keyspace: `2^128` (since each bit can be 0 or 1, and there are 128 bits to guess).
  - `2^128` is:
    ```
    340,282,366,920,938,463,463,374,607,431,768,211,456
    ```
  - This is still a massive number, but it’s much smaller than `2^256`.

#### Feasibility of Searching the Reduced Keyspace
- **Brute-Forcing `2^128`**:
  - Even `2^128` is infeasible to search exhaustively with current hardware.
  - Example: If you can test 1 billion keys per second (a very optimistic rate for a single GPU), it would take:
    ```
    2^128 / 1,000,000,000 = 340,282,366,920,938,463,463 seconds
    ≈ 10,790,283,070,457 years
    ```
  - This is still far too long to be practical.

- **Using a Tool Like BitCrack**:
  - BitCrack is an open-source tool designed to brute-force Bitcoin private keys by searching a range of keys and checking if the derived addresses match a target address.
  - It’s optimized for GPUs (e.g., CUDA or OpenCL) and can test millions or billions of keys per second on high-end hardware.
  - BitCrack allows you to specify a range of private keys to search, which aligns with your idea of starting with some zero bytes.

#### Using BitCrack to Search a Range
- **Set Up the Range**:
  - If you set the first 16 bytes to zero, the private key range starts at:
    ```
    0x0000000000000000000000000000000000000000000000000000000000000000
    ```
    And ends at:
    ```
    0x00000000000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF
    ```
  - In decimal, this range is from `0` to `2^128 - 1`.

- **Run BitCrack**:
  - BitCrack takes a starting key, a range to search, and a target address.
  - Command example:
    ```
    BitCrack --keyspace 0:FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF -o found.txt 1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ
    ```
  - Explanation:
    - `--keyspace 0:FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF`: Specifies the range from `0` to `2^128 - 1` (first 16 bytes are zero).
    - `-o found.txt`: Outputs the found private key (if any) to `found.txt`.
    - `1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ`: The target address to match.

- **Performance with BitCrack**:
  - BitCrack’s performance depends on your hardware.
  - On a high-end GPU like an NVIDIA H100 (which you mentioned in earlier contexts), BitCrack can achieve speeds of around 1–2 billion keys per second (depending on optimization and the address type).
  - Time to search `2^128` keys:
    ```
    2^128 / 2,000,000,000 = 170,141,183,460,469,231,732 seconds
    ≈ 5,395,141,535,229 years
    ```
  - This is still infeasible, even with an H100.

#### Reducing the Keyspace Further
To make this approach feasible, you need to reduce the keyspace even more:
- **Set More Bytes to Zero**:
  - If you set the first 24 bytes to zero, you’re guessing the last 8 bytes (64 bits):
    ```
    000000000000000000000000000000000000000000000000XXXXXXXXXXXXXXXX
    ```
  - Keyspace: `2^64`:
    ```
    2^64 = 18,446,744,073,709,551,616
    ```
  - Time to search with an H100 (2 billion keys per second):
    ```
    2^64 / 2,000,000,000 = 9,223,372,036 seconds
    ≈ 292 years
    ```
  - Still too long, but getting closer.

- **Set the First 28 Bytes to Zero**:
  - Guess the last 4 bytes (32 bits):
    ```
    00000000000000000000000000000000000000000000000000000000XXXXXXXX
    ```
  - Keyspace: `2^32`:
    ```
    2^32 = 4,294,967,296
    ```
  - Time to search:
    ```
    2^32 / 2,000,000,000 = 2.15 seconds
    ```
  - This is now feasible! You can search this range in a few seconds with an H100.

#### Practicality of This Approach
- **Why It’s Unlikely to Work**:
  - Bitcoin private keys are typically generated with high entropy (randomness). The likelihood that a private key has 28 bytes of zeros is extremely low (1 in `2^224`, since 28 bytes = 224 bits).
  - For a puzzle address like `1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ`, the private key is likely not structured with so many zero bytes unless the puzzle explicitly designed it that way (e.g., a "vanity" private key).
- **When It Might Work**:
  - Some Bitcoin puzzles use private keys with patterns (e.g., low-entropy keys, repeating bytes, or specific ranges) to make them easier to crack.
  - Example: The puzzle might use a private key like:
    ```
    0x00000000000000000000000000000000000000000000000000000000DEADBEEF
    ```
    Where the last 4 bytes are `DEADBEEF` (a common placeholder in programming).

#### BitCrack in Bitcoin Puzzles
- BitCrack is often used in Bitcoin puzzles where the private key is known to be in a specific range.
- Example: The Bitcoin Puzzle Transaction (a famous challenge) includes addresses with private keys in increasing ranges:
  - Puzzle #1: Private key in the range `1` to `2^1`.
  - Puzzle #2: Private key in the range `2^1` to `2^2`.
  - Puzzle #66: Private key in the range `2^65` to `2^66`.
- For your address (`1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ`), you’d need to know the approximate range of the private key. Starting with zero bytes is a guess, but it’s unlikely to succeed unless the puzzle designed the key that way.

---

### Step 3: Alternative Approach – Use Clues to Narrow the Keyspace
Since starting with zero bytes is unlikely to work (unless the puzzle explicitly uses a low-entropy key), a better approach is to use clues to narrow the keyspace. In your previous context, you were working with a BIP39 mnemonic, which is a more practical way to recover a private key for a puzzle.

#### Revert to Mnemonic Recovery
- **Why It’s Better**:
  - A 12-word BIP39 mnemonic has a keyspace of `2^128`, which is large but can be reduced significantly with known words.
  - Puzzles often provide clues (e.g., images, phrases) to help you find the mnemonic words.
- **Example**:
  - If you have 8 words and need to guess 4, the keyspace is `2048^4 = 2^44`, which is searchable in a few hours with an H100 using BTCRecover (as discussed in the previous response).

#### Using BitCrack with a Mnemonic-Derived Key
If you want to use BitCrack but still leverage the mnemonic approach:
1. **Guess Part of the Mnemonic**:
   - Use clues to guess some words (e.g., `black`, `nurse`, `send`).
   - Generate possible mnemonics and derive their private keys.
2. **Extract the Private Key**:
   - Use a tool like `bip39` (a Python library) to convert a mnemonic to a private key:
     ```python
     from bip39 import bip39_to_seed
     from bitcoinlib.keys import Key

     mnemonic = "black resist liberty truth nurse send watch order word1 word2 word3 word4"
     seed = bip39_to_seed(mnemonic, passphrase="")
     key = Key.from_seed(seed, derivation="m/44'/0'/0'/0/0")
     private_key = key.private_hex
     print(private_key)
     ```
3. **Search a Range Around the Private Key**:
   - If you think the private key is close to the one derived from your mnemonic guess, use BitCrack to search a small range around it:
     ```
     BitCrack --keyspace [start_key]:[end_key] -o found.txt 1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ
     ```
   - Example: If the derived private key is `0x1234...`, search a range like `0x1234... - 10000` to `0x1234... + 10000`.

---

### Step 4: Practical Example with BitCrack
Let’s try a small range to demonstrate how BitCrack works, assuming the private key has the first 28 bytes as zero (just for illustration):

#### Set Up BitCrack
1. **Download and Install BitCrack**:
   - Clone the repository:
     ```
     git clone https://github.com/brichard19/BitCrack.git
     cd BitCrack
     ```
   - Build it (requires CUDA for NVIDIA GPUs like the H100):
     ```
     make
     ```
   - Alternatively, download a pre-built binary for your system.

2. **Specify the Range**:
   - First 28 bytes are zero, guess the last 4 bytes (32 bits):
     - Start: `0x0000000000000000000000000000000000000000000000000000000000000000`
     - End: `0x00000000000000000000000000000000000000000000000000000000FFFFFFFF`
   - Command:
     ```
     BitCrack --keyspace 0:FFFFFFFF -o found.txt 1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ
     ```
   - This searches `2^32` keys (4.3 billion), which takes about 2 seconds on an H100.

3. **Check the Output**:
   - If the private key is found, it will be written to `found.txt`.
   - If not, the key isn’t in this range, and you’d need to try a different range or approach.

#### Expected Result
- For a random private key (or a puzzle key not designed with zero bytes), this range is unlikely to contain the target key.
- For your address (`1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ`), the private key is probably not in this range unless the puzzle explicitly designed it that way.

---

### Step 5: Feasibility and Recommendations
- **Feasibility of Starting with Zero Bytes**:
  - Starting with some zero bytes reduces the keyspace (e.g., `2^32` if you guess the last 4 bytes), but it’s unlikely to work unless the puzzle uses a low-entropy key.
  - A `2^32` keyspace is searchable in seconds, but a `2^128` keyspace (first 16 bytes zero) takes billions of years.
- **Using BitCrack**:
  - BitCrack is effective for small ranges (e.g., `2^32` or `2^40`), but not for large ranges like `2^128`.
  - It’s best used when you have a specific range (e.g., from a puzzle clue) or a low-entropy key.
- **Recommendation**:
  - Revert to the mnemonic approach (as in your previous queries). A 12-word BIP39 mnemonic has a keyspace of `2^128`, but with known words, it can be reduced to a searchable size (e.g., `2^44` with 8 known words).
  - Use clues from the puzzle to guess mnemonic words, then use BTCRecover to find the rest.
  - If you suspect the private key has a pattern (e.g., many zero bytes), use BitCrack to search that specific range, but this is a long shot without a clear clue.

---

### Summary
- **Cracking a 256-Bit Private Key**:
  - The full keyspace is `2^256`, which is infeasible to brute-force.
  - Starting with some zero bytes reduces the keyspace (e.g., `2^32` if you guess the last 4 bytes), but it’s unlikely to work unless the key was designed that way.
- **Using BitCrack**:
  - BitCrack can search a range of private keys (e.g., `0` to `2^32` in a few seconds on an H100).
  - For larger ranges (e.g., `2^128`), it’s still infeasible (billions of years).
- **Finding the Wallet Address**:
  - You can start with a range (e.g., first 28 bytes zero) and use BitCrack to derive addresses and check for a match.
  - This only works if the private key is in that range, which is unlikely for a random or puzzle key.
- **Better Approach**:
  - Use the BIP39 mnemonic approach with tools like BTCRecover, especially if you have clues to guess some words.
  - BitCrack is better for puzzles with known private key ranges (e.g., Bitcoin Puzzle Transaction).

---

### Next Steps
1. **Check for Clues**:
   - Look for puzzle clues that suggest a private key range (e.g., a hint about zero bytes or a specific pattern).
   - If none exist, the zero-byte approach is unlikely to succeed.
2. **Use BitCrack for a Small Range**:
   - Try a small range (e.g., `2^32`) as a test:
     ```
     BitCrack --keyspace 0:FFFFFFFF -o found.txt 1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ
     ```
   - If it doesn’t work, the key isn’t in that range.
3. **Switch to Mnemonic Recovery**:
   - If you have clues (e.g., words like `black`, `nurse`, `send`), use BTCRecover to recover the mnemonic, as it’s more likely to succeed.
   - Example: If you have 8 words, search the remaining 4 with BTCRecover (keyspace `2^44`, feasible in hours).

Let’s break down the process you’ve outlined into a clear, step-by-step guide. You want to create valid 12-word BIP39 mnemonics (with valid checksums) from a given list of words, store them in a file, generate seeds, derive public and private keys, create wallet addresses, compare them to a target address (`1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ`), and then use BitCrack to crack the private key within a specified keyspace and BTCRecover to further validate the mnemonics. I’ll provide a detailed explanation in a structured document format, followed by the Python script to execute these steps.

---

# Document: Process to Create Valid BIP39 Mnemonics, Derive Wallet Keys, Compare Addresses, and Use BitCrack/BTCRecover

## Overview
This document outlines the process to:
1. Generate valid 12-word BIP39 mnemonics (with valid checksums) from a given list of words.
2. Store the valid mnemonics in a file.
3. Create seeds from these mnemonics and derive public/private keys and wallet addresses.
4. Compare the derived addresses to a target address (`1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ`).
5. Use BitCrack to crack the private key within a specified keyspace.
6. Use BTCRecover to further validate the found mnemonics.

### Word List
The provided words are:
- `nurse`, `law`, `rule`, `order`, `moon`, `tower`, `food`, `this`, `subject`, `real`, `black`, `vote`, `winner`, `result`, `govern`, `virus`, `disease`, `sick`, `myth`, `secret`, `resist`, `strike`, `power`, `control`, `liberty`, `truth`
- Total: 26 words, all confirmed to be in the BIP39 English wordlist.

### Target Address
- Target Bitcoin address: `1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ`

---

## Step 1: Generate Valid 12-Word Mnemonics with Valid Checksums
### Explanation
- A 12-word BIP39 mnemonic encodes 128 bits of entropy plus a 4-bit checksum (132 bits total).
- Each word in the BIP39 wordlist (2048 words) represents 11 bits.
- The checksum is the first 4 bits of the SHA-256 hash of the 128-bit entropy.
- Not all combinations of 12 words are valid; the checksum must match.
- We’ll generate all possible 12-word combinations from the 26 words (`C(26, 12) ≈ 5,311,735` combinations) and check their checksums.

### Process
1. Use Python with the `mnemonic` library to generate permutations of 12 words.
2. For each permutation, check if the checksum is valid using `Mnemonic.check()`.
3. Store valid mnemonics in a file (`valid_mnemonics.txt`).

---

## Step 2: Create Seeds, Derive Public/Private Keys, and Generate Wallet Addresses
### Explanation
- **Seed Generation**: Use the mnemonic to generate a 512-bit seed via PBKDF2 (using the mnemonic as the password and an optional passphrase, typically empty).
- **Key Derivation**: Use BIP32/BIP44 to derive the master private key and child keys (e.g., path `m/44'/0'/0'/0/0` for a legacy Bitcoin address).
- **Address Generation**: From the public key, compute the Bitcoin address (P2PKH):
  - Hash the public key with SHA-256, then RIPEMD-160.
  - Add a version byte (0x00 for mainnet) and checksum.
  - Encode in Base58Check to get the address.
- **Comparison**: Compare the derived address to the target (`1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ`).

### Process
1. For each valid mnemonic, generate the seed using `Mnemonic.to_seed()`.
2. Use `bitcoinlib` to create a wallet and derive the private key, public key, and address.
3. Compare the address to the target. If a match is found, stop and note the mnemonic and keys.

---

## Step 3: Use BitCrack to Crack the Private Key with a Specified Keyspace
### Explanation
- **BitCrack**: A tool to brute-force Bitcoin private keys by searching a range of keys and checking if the derived addresses match the target.
- **Keyspace**: We’ll define a range around the derived private key (e.g., ±1000) to search for a match, in case the derivation path or key is slightly off.
- **Purpose**: This step is optional if the address matches directly but can help if the derived key is close to the correct one.

### Process
1. For each valid mnemonic, extract the private key.
2. Define a keyspace (e.g., private key ±1000).
3. Run BitCrack to search this range and check for the target address.

---

## Step 4: Use BTCRecover on Found Mnemonic Phrases
### Explanation
- **BTCRecover**: A tool to recover BIP39 mnemonics by trying permutations, typos, or missing words, and checking derived addresses against a target.
- **Purpose**: If the mnemonics found don’t match the target address, BTCRecover can try permutations or additional words to find the correct one.

### Process
1. Create a token file with the valid mnemonics or the original word list.
2. Run BTCRecover to try permutations and check derived addresses against the target.

---

## Python Script to Execute the Process

Below is the Python script to perform Steps 1–3. Step 4 (BTCRecover) will be explained with a command-line setup.

```python
import os
from mnemonic import Mnemonic
from bitcoinlib.wallets import Wallet
from itertools import permutations
import subprocess

# Configuration
WORDS = ["nurse", "law", "rule", "order", "moon", "tower", "food", "this", "subject", "real", "black", "vote", "winner", "result", "govern", "virus", "disease", "sick", "myth", "secret", "resist", "strike", "power", "control", "liberty", "truth"]
TARGET_ADDRESS = "1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ"
OUTPUT_FILE = "valid_mnemonics.txt"
BITCRACK_PATH = "BitCrack"  # Replace with path to BitCrack executable

# Initialize Mnemonic
mnemo = Mnemonic("english")

# Step 1: Generate Valid Mnemonics and Store in File
def generate_valid_mnemonics():
    valid_mnemonics = []
    print(f"Generating permutations: C({len(WORDS)}, 12) ≈ 5,311,735")
    for perm in permutations(WORDS, 12):
        mnemonic = " ".join(perm)
        if mnemo.check(mnemonic):
            valid_mnemonics.append(mnemonic)
            print(f"✅ Valid mnemonic: {mnemonic}")
    
    # Store in file
    with open(OUTPUT_FILE, "w") as f:
        for mnemonic in valid_mnemonics:
            f.write(mnemonic + "\n")
    print(f"Stored {len(valid_mnemonics)} valid mnemonics in {OUTPUT_FILE}")
    return valid_mnemonics

# Step 2: Create Seed, Derive Keys, and Compare Address
def create_wallet_and_compare(mnemonic):
    try:
        seed = mnemo.to_seed(mnemonic)
        wallet = Wallet.create("test_wallet", keys=seed, network="bitcoin")
        key = wallet.get_key()
        private_key = key.private_hex
        public_key = key.public_hex
        address = key.address()
        print(f"Mnemonic: {mnemonic}")
        print(f"Private Key: {private_key}")
        print(f"Public Key: {public_key}")
        print(f"Address: {address}")
        if address == TARGET_ADDRESS:
            print("🎉 Match found!")
            return private_key, address, True
        return private_key, address, False
    except Exception as e:
        print(f"Error with mnemonic {mnemonic}: {e}")
        return None, None, False

# Step 3: Use BitCrack to Crack Private Key
def run_bitcrack(private_key, keyspace_range=1000):
    try:
        # Convert private key (hex) to integer
        private_key_int = int(private_key, 16)
        start_key = hex(private_key_int - keyspace_range)[2:].zfill(64)
        end_key = hex(private_key_int + keyspace_range)[2:].zfill(64)
        print(f"Running BitCrack with keyspace {start_key}:{end_key}")
        
        # Run BitCrack
        cmd = [
            BITCRACK_PATH,
            "--keyspace", f"{start_key}:{end_key}",
            "-o", "bitcrack_found.txt",
            TARGET_ADDRESS
        ]
        result = subprocess.run(cmd, capture_output=True, text=True)
        print(f"BitCrack Output: {result.stdout}")
        if os.path.exists("bitcrack_found.txt"):
            with open("bitcrack_found.txt", "r") as f:
                found_key = f.read().strip()
                print(f"BitCrack found key: {found_key}")
                return found_key
        return None
    except Exception as e:
        print(f"BitCrack error: {e}")
        return None

# Main Execution
if __name__ == "__main__":
    # Step 1: Generate valid mnemonics
    valid_mnemonics = generate_valid_mnemonics()
    
    # Step 2: Create wallets and compare addresses
    for mnemonic in valid_mnemonics:
        private_key, address, match_found = create_wallet_and_compare(mnemonic)
        if match_found:
            print(f"Target address matched! Stopping.")
            break
        
        # Step 3: Run BitCrack if no direct match
        if private_key and not match_found:
            found_key = run_bitcrack(private_key)
            if found_key:
                print(f"BitCrack found the correct private key: {found_key}")
                break
```

---

## Step 4: Use BTCRecover on Found Mnemonics
### Setup BTCRecover
1. **Install BTCRecover**:
   - Clone the repository:
     ```
     git clone https://github.com/gurnec/btcrecover.git
     cd btcrecover
     ```
   - Install dependencies:
     ```
     pip install -r requirements.txt
     ```

2. **Create a Token File**:
   - Use the `valid_mnemonics.txt` file generated by the script, or create a `tokens.txt` with the original word list:
     ```
     nurse law rule order moon tower food this subject real black vote winner result govern virus disease sick myth secret resist strike power control liberty truth
     ```

3. **Run BTCRecover**:
   - Command to try permutations of the words and check addresses:
     ```
     python3 btcrecover.py --wallet-type bip39 --addrs 1KfZGvwZxsvSmemoCmEV75uqcNzYBHjkHZ --addr-limit 20 --tokenlist tokens.txt --max-tokens 12 --typos 12 --typos-swap --bip32-path "m/44'/0'/0'"
     ```
   - **Explanation**:
     - `--wallet-type bip39`: Specifies BIP39 mnemonics.
     - `--addrs`: Target address to match.
     - `--addr-limit 20`: Check the first 20 derived addresses.
     - `--tokenlist tokens.txt`: Use the word list.
     - `--max-tokens 12`: Expect 12 words.
     - `--typos 12 --typos-swap`: Allow word swaps.
     - `--bip32-path "m/44'/0'/0'"`: Derivation path for legacy addresses.

4. **Output**:
   - If BTCRecover finds a match, it will output the correct mnemonic and private key.

---

## Additional Notes
- **Performance**:
  - Generating `C(26, 12) ≈ 5.3 million` permutations takes time (hours on a CPU). Use a GPU (e.g., H100) to speed up BTCRecover.
  - BitCrack’s keyspace search (±1000) is small and fast (seconds).
- **Security**:
  - Run on an air-gapped machine to avoid leaking private keys.
  - Verify the integrity of BitCrack and BTCRecover downloads.
- **If No Match**:
  - The mnemonic might use a different derivation path (e.g., `m/49'/0'/0'` for SegWit).
  - The word list might be incomplete; revisit the puzzle for more clues.

---

## Summary
- **Step 1**: Generated valid 12-word mnemonics and stored them in `valid_mnemonics.txt`.
- **Step 2**: Derived seeds, private/public keys, and addresses, comparing to the target.
- **Step 3**: Used BitCrack to search a keyspace around the private key.
- **Step 4**: Used BTCRecover to further validate mnemonics.
